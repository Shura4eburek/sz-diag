# SzDiag Desk — итоги спайка stream-json

**Дата:** 2026-09-25 · `claude` 2.1.282 · Windows 11, сервисный бокс.
Фикстуры — `tests/SzDiag.Claude.Tests/Fixtures/*.jsonl` (сырой stdout, одна строка — одно
событие). В фикстурах обрезаны только объёмные поля окружения: `hook_response.output/stdout`
(полный текст скиллов из SessionStart-хука), `commands_changed.commands`, списки
`skills`/`slash_commands`/`plugins` и облачные MCP-серверы в `init` — остальное как было.

| Фикстура | Что снято |
|---|---|
| `simple-turn.jsonl` | один ход без инструментов |
| `tool-turn.jsonl` | ход с вызовом инструмента (`PowerShell`) |
| `permission-turn.jsonl` | `--permission-prompt-tool`, ответ `allow` → файл создан |
| `permission-deny-turn.jsonl` | то же, ответ `deny` → отказ в ленте |
| `interrupt-turn.jsonl` | прерывание хода control-запросом + следующий ход в том же процессе |
| `resume-init.jsonl` | `--resume` сессии из `simple-turn` |

## 1. Схема событий

Каждая строка — объект с `type` (и часто `subtype`), у всех есть `session_id` и `uuid`.

- `system/hook_started`, `system/hook_response` — хуки SessionStart (у нас — superpowers).
  Приходят **до** `init`. Для ленты — шум, для диагностики — полезно (`exit_code`, `stderr`).
- `system/init` — `session_id`, `model`, `cwd`, `tools[]`, `mcp_servers[]` (`name`, `status`:
  `pending`/`connected`/`needs-auth`…), `permissionMode`, `claude_code_version`,
  `capabilities[]` (есть `interrupt_receipt_v1`), `apiKeySource`. **Приходит на каждый ход**,
  а не один раз на процесс (в `interrupt-turn` второй ход снова дал `init`).
- `system/commands_changed`, `system/thinking_tokens` (`estimated_tokens`), `rate_limit_event`
  (`rate_limit_info.status`, `utilization`) — служебные, в ленту не идут; `rate_limit_event`
  пригодится статусбару.
- `assistant` — `message` в формате Messages API (`id`, `model`, `content[]`, `usage`),
  плюс `parent_tool_use_id` (не null — событие субагента), `request_id`, `timestamp`.
  **Одна строка — один блок контента:** блоки `thinking`, `text`, `tool_use` одного ответа
  приходят отдельными строками с одинаковым `message.id`. Блок `tool_use`: `id`, `name`,
  `input`, `caller`. Блок `thinking` в нашем режиме приходит с пустым `thinking` и `signature`.
- `user` — `message.content[]` с блоками `tool_result` (`tool_use_id`, `content` — строка,
  `is_error` — может отсутствовать при успехе) + вне `message`: `tool_use_result`
  (структурированный результат: у Bash/PowerShell `stdout`/`stderr`/`interrupted`, у Write
  `type`/`filePath`, при ошибке — строка) и при отказе `tool_result_meta[]`
  (`non_execution_kind: "permission-rule"`). При прерывании приходит `user` с блоком
  `text` = `[Request interrupted by user]`.
- `result` — итог хода: `subtype` (`success` / `error_during_execution`), `is_error`,
  `result` (финальный текст, при прерывании `null`), `stop_reason`, `terminal_reason`
  (`completed` / `aborted_streaming`), `num_turns`, `duration_ms`, `duration_api_ms`,
  `total_cost_usd`, `usage` (`input_tokens`, `output_tokens`, `cache_read_input_tokens`,
  `cache_creation_input_tokens`), `modelUsage{<модель>: {inputTokens, outputTokens,
  costUSD, contextWindow…}}`, `permission_denials[]` (`tool_name`, `tool_use_id`,
  `tool_input`), `queued_turn_count`.
- `control_response` — ответ на control-запрос (см. п. 4).

## 2. `permission_prompt`

- **Аргументы:** `tool_name` (строка), `input` (объект — вход инструмента), `tool_use_id`.
- **Ответ** — текст (JSON строкой) в результате MCP-инструмента:
  `{"behavior":"allow","updatedInput":<input>}` или `{"behavior":"deny","message":"…"}`.
  На `deny` в ленте `tool_result` с `is_error: true` и `content` = наше `message`, в
  `result.permission_denials` — запись об отказе.
- **Режим разрешений.** У пользователя по умолчанию `permissionMode: auto` — в нём
  `permission_prompt` **не вызывается вовсе** (Write прошёл без вопроса). Desk обязан
  передавать `--permission-mode default` явно, иначе карточек разрешений не будет.
- **Долгий ответ.** 150 с — ждёт и продолжает нормально. **Больше 300 с молчания — обрыв:**
  `MCP server "desk" tool "permission_prompt" sent no response or progress for 300s;
  aborting`, инструмент получает ошибку, ход продолжается без него. Лечится полем
  `"timeout"` (мс) в описании сервера в `--mcp-config` (или progress-уведомлениями из
  тулзы). Проверено: с `"timeout": 86400000` ответ через 330 с принят, файл создан — Desk
  ставит этот таймаут (сутки) и получает «ждём как терминал».

## 3. Путь `/mcp/<ключ>`

Работает: `claude` ходит ровно на URL из конфига (`POST /mcp/161432`), заголовки из
`headers` доезжают (`X-Desk-Token`), ключ из маршрута виден в тулзе
(`HttpContext.Request.RouteValues["key"]`, сервер в режиме `Stateless`). Различать сессии
по пути можно. SDK: `ModelContextProtocol.AspNetCore` 2.2.0, `MapMcp("/mcp/{key}")`.

## 4. Прерывание

Control-запрос **работает**, процесс **остаётся жив**:
```json
{"type":"control_request","request_id":"int-1","request":{"subtype":"interrupt"}}
```
Ответ мгновенный: `{"type":"control_response","response":{"subtype":"success",
"request_id":"int-1","response":{"still_queued":[]}}}`, затем дописанный `assistant`,
`user` с `[Request interrupted by user]` и `result` с `subtype: error_during_execution`,
`terminal_reason: aborted_streaming`. Следующее сообщение в stdin обрабатывается тем же
процессом как обычный ход. Запасной путь (kill + `--resume`) не нужен.

## 5. `--resume`

`session_id` **сохраняется**: `--resume 8c8e…` дал `init` с тем же `8c8e…`, разговор
продолжился («Ты просил ответить одним словом: «привет»»). В `desk-sessions.json` хранить
`session_id` из первого `init` — он стабилен.

## 6. Видимость для `claude-tg-bridge`

Headless-процесс **регистрируется** в `<CLAUDE_CONFIG_DIR>/sessions/<pid>.json` так же, как
интерактивный: `kind: "interactive"`, `entrypoint: "sdk-cli"`, `sessionId`, `cwd`, `status`
(`busy`), `messagingSocketPath`. Мост, читающий этот каталог, сессию Desk увидит; отличить
её можно по `entrypoint`.

## Что поменялось в спеке

- `ClaudeProcess`: + `--permission-mode default`; в `--mcp-config` — `timeout` сервера (п. 2).
- «Стоп» — control-запрос `interrupt` подтверждён, запасной путь убран.
- Парсер: `init` на каждый ход; блоки одного ответа — отдельными строками.
