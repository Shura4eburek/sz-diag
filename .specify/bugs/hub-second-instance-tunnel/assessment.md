# Bug Assessment: второй экземпляр hub гасит туннель живого, Desk из исходников не видит hub

- **Slug**: hub-second-instance-tunnel
- **Created**: 2026-10-19
- **Source**: pasted text (оператор: «хаб сдох… ошибка вылезла, из-за чего деск потерял связь с хабом») + логи
- **Verdict**: valid
- **Severity**: high

## Report (verbatim or summarized)

Оператор запустил Desk из исходников (`dotnet run --project src/SzDiag.Desk`) при работающем hub из
`dist`. Вылезло окно с ошибкой, Desk показывал «hub не отвечает».

Логи:
- `src/SzDiag.Desk/bin/Debug/net8.0/logs/desk-2026-10-19.log`: `15:01:26 старт, hub http://localhost:5000` →
  `hub не отвечал — запущен …\dist\host\start-hub.cmd` (то же в 15:05 и 15:53).
- `dist/host/hub/logs/hub-20261019.log`: `туннель: добит cloudflared от прошлого запуска` →
  `Failed to bind to address http://0.0.0.0:5099: address already in use` →
  `ThreadPoolWatchdog … OperationCanceledException` → `crit … BackgroundServiceExceptionBehavior is configured to StopHost`;
  живой hub: `туннель: cloudflared запущен (pid 38424)` в 15:53 — перезапуск после убийства.

## Symptom

Desk из исходников никогда не видит hub из `dist` и на каждом старте запускает второй hub. Второй hub,
прежде чем упасть на занятом порту, убивает `cloudflared` живого hub — `hub.mamoru.fun` пропадает,
пока живой hub не перезапустит туннель (≥10 с), а окно второго hub показывает crit-ошибку.
Ожидалось: Desk из исходников подключается к hub на этом боксе; второй hub выходит, ничего не трогая.

## Reproduction

1. Запустить hub из `dist\host` (`start-hub.cmd`, порт 5099, туннель включён).
2. `dotnet run --project src/SzDiag.Desk` — Desk читает `src/SzDiag.Desk/appsettings.json`
   (`HubBaseUrl: http://localhost:5000`, `ManagementToken: dev-token`).
3. Desk: `/healthz` на 5000 не отвечает → `HubAutostart` запускает `dist\host\start-hub.cmd`.
4. Второй hub: `HubTunnelService.ExecuteAsync` → `KillOrphan()` по `pid`-файлу убивает cloudflared живого
   hub; затем Kestrel падает на 5099; при остановке `ThreadPoolWatchdog` бросает OCE → crit.

## Suspected Code Paths

- `src/SzDiag.Desk/appsettings.json` / `Services/DeskOptions.cs:7` — дефолт `http://localhost:5000` и `dev-token`, а hub по умолчанию слушает 5099 (`Hub/Program.cs:29`, `build-dist` Port=5099).
- `src/SzDiag.Desk/Services/DeskOptions.cs:Load()` — нет локального оверрайда для запуска из исходников: реальный токен hub лежит только в `dist\host\desk\appsettings.json`.
- `src/SzDiag.Hub/HubTunnelService.cs:110 KillOrphan()` — запускается в `StartAsync` до бинда Kestrel; pid-файл не различает «сирота от упавшего hub» и «туннель живого hub».
- `src/SzDiag.Hub/Program.cs` — нет защиты от второго экземпляра на том же порту.
- `src/SzDiag.Hub/ThreadPoolWatchdog.cs:45` — `WaitForNextTickAsync(stoppingToken)` бросает OCE при отмене; при неудачном старте `StopAsync` не вызывался, и хост считает это падением службы (crit).

## Root Cause Hypothesis

Две независимые причины, сложившиеся в один инцидент (уверенность: high — подтверждено логами и кодом).
(1) Конфиг Desk в исходниках указывает на dev-порт 5000 с dev-токеном, а единственный hub на боксе — из
`dist` на 5099 со своим токеном; автозапуск Desk лечит «не отвечает» запуском второго hub. (2) Hub не
проверяет, что он единственный на своём порту, а `KillOrphan` выполняется раньше бинда порта и убивает
туннель чужого живого экземпляра. Шум crit — отдельная мелкая ошибка `ThreadPoolWatchdog`.

## Proposed Remediation

**Preferred**:
- Hub: в `Program.cs` до построения хоста захватить именованный мьютекс по порту прослушивания
  (`Local\SzDiag.Hub-<port>`); занят — строка «hub на порту N уже запущен» и выход с ненулевым кодом,
  ни одна hosted-служба (туннель, бэкап kb) не стартует.
- `ThreadPoolWatchdog`: отмену `stoppingToken` считать штатным выходом (поймать OCE).
- Desk: дефолт `HubBaseUrl` → `http://localhost:5099` (порт hub по умолчанию); `DeskOptions.Load` поверх
  `appsettings.json` читает необязательный `appsettings.local.json` (в `.gitignore`, копируется в bin при
  наличии) — туда на боксе кладутся адрес и токен своего hub для запуска из исходников.

**Alternatives**:
- `KillOrphan` после бинда порта (через `IHostApplicationLifetime.ApplicationStarted`) — закрывает только
  убийство туннеля, второй hub всё равно поднимает бэкап kb и прочие службы до падения.
- Desk читает конфиг `dist\host\desk\appsettings.json` при запуске из исходников — неявная связь с dist.

**Files likely to change**:
- `src/SzDiag.Hub/Program.cs`, `src/SzDiag.Hub/ThreadPoolWatchdog.cs`
- `src/SzDiag.Desk/Services/DeskOptions.cs`, `src/SzDiag.Desk/appsettings.json`, `src/SzDiag.Desk/SzDiag.Desk.csproj`, `.gitignore`
- тесты: `tests/SzDiag.Hub.Tests`, `tests/SzDiag.Desk.Tests`

**Tests to add or update**:
- Hub: второй захват мьютекса на том же порту не удаётся, на другом — удаётся (вынести в класс `HubInstanceLock`).
- Hub: `ThreadPoolWatchdog` завершается без исключения при отмене токена.
- Desk: `appsettings.local.json` перекрывает `HubBaseUrl`/`ManagementToken`; без него — дефолт 5099.

## Risks & Considerations

- Мьютекс `Local\` — в пределах сеанса пользователя; hub, запущенный в другом сеансе (служба), не увидит
  первый — тогда остаётся старое поведение (падение на бинде). Приемлемо: на боксе hub запускают руками.
- `appsettings.local.json` содержит management-токен — только в `.gitignore`, не в репо.
- Смена дефолтного порта Desk затрагивает только запуск без конфига; dist-конфиг пишет `build-dist`.

## Open Questions

- Нет.
