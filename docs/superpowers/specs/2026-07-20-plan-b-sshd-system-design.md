# План Б: портативный sshd под LocalSystem

**Дата:** 2026-07-20
**Статус:** дизайн одобрен, ждёт ревью спеки

## Проблема

`PortableSshServer.Start` поднимает `sshd.exe` **дочерним процессом** админ-агента.
Агент — администратор, но **не LocalSystem**. Для создания logon-токена входящего
пользователя при publickey-аутентификации sshd нужен `SeTcbPrivilege`, который есть
только у LocalSystem. Итог: sshd слушает, KEX проходит, но сессия рвётся в самом начале
userauth — `Connection reset`.

Подтверждено вживую 2026-07-20 на СЗ 159873 (`192.0.2.10`): `ssh -vv` рвётся сразу
после `SSH2_MSG_SERVICE_ACCEPT`, до отправки ключа (не `Permission denied (publickey)` —
значит дело не в ключе, а в токене). Симптом в `sshd.log` — `unable to create logon
token`. Этот риск был предсказан в спеке портативного OpenSSH
([2026-07-07-portable-openssh-design.md](2026-07-07-portable-openssh-design.md), раздел
«Риск для валидации») — сейчас срабатываем фолбэк (план Б), который там же и заложен.

## Цель

sshd на клиенте работает под **LocalSystem**, publickey-логин `svc-diag` создаёт
logon-токен, `ssh svc-diag@<IP> whoami` возвращает `<машина>\svc-diag`. Остальное
(свои host-ключи каждую сессию, свой конфиг, fail-closed, откат без следов) сохраняется.

## Решение: sshd транзиентной scheduled task под SYSTEM

Вместо `Process.Start(sshd.exe)` регистрируем и запускаем **scheduled task** с
`-User 'SYSTEM' -RunLevel Highest` — тем же паттерном, каким `WindowsSystemAccessManager.Open`
уже регистрирует watchdog-задачу (шаг 7). Задача держит `sshd.exe -D` живым под
LocalSystem; чистится тем же watchdog-механизмом при закрытии СЗ.

### Почему именно так

- **SeTcbPrivilege есть** — LocalSystem владеет им по умолчанию, sshd создаёт
  logon-токен, симптом исчезает.
- **Паттерн уже в проекте** — `Register-ScheduledTask -User 'SYSTEM'` обкатан на
  watchdog. Минимум нового и рискового кода.
- **Fail-closed сохраняется** — задача транзиентная, снимается на откате (клавиша `C`,
  команда `close`, либо watchdog по таймауту). После reboot следы уберёт watchdog.

## Компоненты

### 1. `IPowerShellRunner` (новый интерфейс)

`PowerShellRunner` абстрагируется интерфейсом `IPowerShellRunner` (метод
`Run(script, throwOnError, timeout)`), чтобы оркестрацию задачи можно было покрыть
юнит-тестами на фейке. `PowerShellRunner : IPowerShellRunner` — реализация без
изменений. Зависимости `PortableSshServer`, `WindowsSystemAccessManager`,
`PowerShellCommandExecutor` переводятся на интерфейс. `Program.cs` конструирует конкрет.

### 2. `PortableSshServer` — запуск под SYSTEM вместо дочернего процесса

`Start` и `Stop` больше не оперируют `Process`-хендлом (под задачей процесс не дочерний).

- **`Start(int port, string authorizedKeyLine, string taskName)`** — добавлен `taskName`
  (`szdiag-sshd-{Sz}`, из `RevertState`):
  1. host-ключи, `authorized_keys`, `sshd_config`, ACL, удаление старого лога — **без
     изменений**;
  2. регистрирует scheduled task: action `sshd.exe -f <config> -D -E <log>`,
     `-User 'SYSTEM' -RunLevel Highest -Force`, settings `ExecutionTimeLimit 0`
     (без лимита), `MultipleInstances IgnoreNew`, `AllowStartIfOnBatteries`,
     `DontStopIfGoingOnBatteries`; затем `Start-ScheduledTask`;
  3. **готовность через поллинг порта** (не `WaitForExit`): до ~5 c пробует
     `TcpClient` на `127.0.0.1:port`. Успешный connect = слушает → готово. Тайм-аут →
     читает `<log>`, бросает `SshdStartException` с `DescribeFailure(log)`.
- **`Stop(string taskName)`** — идемпотентно, работает и когда агент уже мёртв
  (watchdog-ревёрт):
  1. `Stop-ScheduledTask` + `Unregister-ScheduledTask` (throwOnError: false);
  2. добить `sshd.exe`, чей `CommandLine` содержит наш `ConfigPath`
     (`Get-CimInstance Win32_Process` → `Stop-Process`) — системный sshd не трогаем,
     он ссылается на чужой конфиг.
- **Билдеры команд статикой** (`BuildRegisterTaskCommand`, `BuildStopCommand`) —
  юнит-тестируются как `BuildConfig`/`DescribeFailure` (проверяем `-User 'SYSTEM'`,
  наличие `taskName`, `ConfigPath` в фильтре). Реальный запуск — ручной e2e.

### 3. `WindowsSystemAccessManager` — прокинуть taskName

- В `Open` (шаг 6): `_sshd.Start(spec.SshPort, keyLine, state.SshdTaskName)`; ставим
  `state.CreatedSshdTask = true` и `Persist()` **сразу после успешного Start** (до этого
  задачи нет — откатывать нечего).
- В `Revert`: под флагом `state.CreatedSshdTask` зовём `_sshd.Stop(state.SshdTaskName)`.
  Порядок — сразу после снятия watchdog-задачи, перед firewall (обратный порядку `Open`).
  Удаление workdir (host-ключи/конфиг) остаётся под `GeneratedHostKeys`.

### 4. `RevertState` — новый флаг и имя задачи

- `string SshdTaskName` (`szdiag-sshd-{Sz}`, задаётся в `Open` рядом с
  `WatchdogTaskName`);
- `bool CreatedSshdTask`.

`GeneratedHostKeys`/`WroteAuthorizedKey` остаются. `_proc`-поле и `SshdStartException`
из-за дочернего процесса — `_proc` удаляется, исключение сохраняется.

## Ключевой инвариант (не нарушать)

Новый шаг `Open` (`CreatedSshdTask`) имеет парную ветку в `Revert` под своим флагом →
на клиенте не остаётся ни задачи, ни живого sshd. Проверяется тремя путями отката
(клавиша `C` / `close` с хоста / watchdog по таймауту) — все идут через один `Revert`.

## Тестирование

- **`PortableSshServer`** — юниты: `BuildRegisterTaskCommand` (`-User 'SYSTEM'`,
  `taskName`, args sshd), `BuildStopCommand` (`Unregister`, `ConfigPath` в фильтре).
  Существующие тесты `BuildConfig`/`DescribeFailure` не трогаем.
- **`WindowsSystemAccessManager`** — с фейком `IPowerShellRunner` и фейком `sshd`:
  `Open` регистрирует sshd-задачу и ставит `CreatedSshdTask`; `Revert` зовёт `Stop`
  под флагом; идемпотентность повторного `Revert`.
- **`RevertState`** — сериализация `SshdTaskName`/`CreatedSshdTask`.
- **Ручной e2e** ([TESTING.md](../../TESTING.md)): онлайн-СЗ, `ssh svc-diag@<IP> whoami`
  → `<машина>\svc-diag`; закрытие СЗ снимает задачу и sshd (следов нет). Это и есть
  проверка token-privilege из плана Б.

## Что НЕ входит (YAGNI)

- Не переходим на временную Windows-службу (`New-Service`) — риск SCM-таймаутов и это
  ближе к «установке» на недоверенный клиент; scheduled task проще и уже обкатана.
- Не кэшируем host-ключи, не меняем hub↔CLI протокол, `SshPort` (22 по умолчанию)
  остаётся.
- Порт-поллинг не заменяем на разбор состояния задачи — connect к слушающему порту
  однозначнее, чем `Ready/Running` гонки.
