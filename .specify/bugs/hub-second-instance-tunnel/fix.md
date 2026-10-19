# Bug Fix: второй экземпляр hub гасит туннель живого, Desk из исходников не видит hub

- **Slug**: hub-second-instance-tunnel
- **Fixed**: 2026-10-19
- **Assessment**: ./assessment.md
- **Status**: applied

## Summary

Hub захватывает именованную блокировку по порту до старта hosted-служб и при занятой выходит с кодом 3,
не трогая туннель живого экземпляра; `ThreadPoolWatchdog` считает отмену штатной остановкой. Desk по
умолчанию смотрит на порт hub (5099) и читает поверх конфига необязательный `appsettings.local.json`.

## Changes

| File | Change | Notes |
|------|--------|-------|
| `src/SzDiag.Hub/HubInstanceLock.cs` | added | именованный семафор `Local\SzDiag.Hub-<port>`, `PortOf(urls)` |
| `src/SzDiag.Hub/Program.cs` | modified | захват после `Build`, до `app.Run`; только для Kestrel; занят → строка + `return 3` |
| `src/SzDiag.Hub/ThreadPoolWatchdog.cs` | modified | `NextTickAsync`: OCE при отмене → `false` |
| `src/SzDiag.Desk/Services/DeskOptions.cs` | modified | дефолт `HubBaseUrl` 5099; `Load(baseDir)` + `appsettings.local.json` |
| `src/SzDiag.Desk/appsettings.json` | modified | 5000 → 5099 |
| `src/SzDiag.Desk/SzDiag.Desk.csproj` | modified | local-конфиг в bin, `CopyToPublishDirectory=Never` |
| `.gitignore` | modified | `appsettings.local.json` |
| `tests/SzDiag.Hub.Tests/HubInstanceLockTests.cs` | added test | второй захват на том же порту отбит, разные порты независимы, разбор порта |
| `tests/SzDiag.Hub.Tests/ThreadPoolWatchdogTests.cs` | added test | отмена тика без исключения |
| `tests/SzDiag.Desk.Tests/DeskOptionsTests.cs` | added test | дефолт 5099; local перекрывает адрес/токен, остальное из базового |

## Tests Added or Updated

- `HubInstanceLockTests.SecondAcquire_SamePort_Refused_UntilFirstReleased` — второй hub на порту не стартует.
- `HubInstanceLockTests.DifferentPorts_Independent`, `PortOf_FirstListenUrl`.
- `ThreadPoolWatchdogTests.NextTick_Cancelled_EndsLoopWithoutException` — нет crit при остановке.
- `DeskOptionsTests.NoConfig_DefaultsToHubPort`, `LocalConfig_OverridesBase`.

## Local Verification

- `dotnet test tests/SzDiag.Hub.Tests` → 293/293.
- `dotnet test tests/SzDiag.Desk.Tests` → 211/211.
- Локально на боксе создан `src/SzDiag.Desk/appsettings.local.json` (копия `dist\host\desk\appsettings.json`, вне git).

## Deviations from Assessment

- Захват блокировки — после `builder.Build()`, а не до построения хоста, и только при `IServer` = Kestrel:
  `WebApplicationFactory` выполняет `Program` целиком, и параллельные тестовые hub'ы отбивали друг друга
  (81 падение). Службы всё равно стартуют только в `app.Run`, так что второй экземпляр их не поднимает.

## Follow-ups

- `KbBackupService` и `OfflineSweeper` ждут `PeriodicTimer` так же, как сторож до правки: при неудачном
  старте по другой причине дадут тот же crit. С блокировкой сценарий 19.10 закрыт; перевести их на
  `NextTickAsync` — отдельной мелкой правкой.
- Перемешанные строки в `hub-<дата>.log` («туннель: clinfo: …») — запись лога не построчно атомарна.
