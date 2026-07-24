# Агент переживает ребут + auto-reconnect под тем же СЗ

**Дата:** 2026-07-24
**Статус:** реализовано (2026-07-24). План: docs/superpowers/plans/2026-07-24-agent-survive-reboot.md

## Проблема

Под нагрузочными тестами клиент ребутается по 5–10 раз за сессию. Каждый ребут
убивает процесс агента: СЗ уходит offline, SSH отваливается, **агента поднимают
вручную** — и он снова спрашивает номер СЗ с консоли. Половина ручных операций при
стресс-диагностике — это «подними агента после вырубона».

Фикс «добить зависший sshd при старте» (коммит `10ceb3f`) — полумера: агента всё
равно надо запустить руками.

## Цель

Агент после ребута машины **сам** поднимается, переподключается к hub под тем же
номером СЗ и продолжает сессию — без ручного вмешательства. Весь механизм выживания
**откатывается без следов** при закрытии СЗ (ключевой инвариант модели угроз).

## Ключевые наблюдения по текущему коду

- **Номер СЗ уже persist'ится.** `RevertState.Sz` пишется в `state.json`
  (`AgentOptions.StatePath`, дефолт `C:\ProgramData\szdiag\state.json`) и переживает
  ребут. Вводить СЗ руками после ребута не нужно — читаем с диска.
- **Что переживает ребут:** firewall-правило, учётка `svc-diag`, `LocalAccountToken­FilterPolicy`
  (реестр), watchdog-таск (постоянная scheduled task). **Что умирает:** транзиентная
  sshd-таск `szdiag-sshd-<СЗ>` (host-ключи в `SshWorkDir` остаются на диске).
  → После ребута надо переподнять **только sshd** + переконнектиться к hub.
- **`Open` НЕ идемпотентен по token policy.** Повторный прогон `Open` после ребута
  прочитает `LocalAccountTokenFilterPolicy = 1` (наше значение) как «исходное»,
  выставит `SetTokenPolicy = false` → при откате policy не восстановится = след.
  Поэтому resume **переиспользует сохранённый `state.json`**, а не запускает `Open`
  заново.
- **SignalR уже с `.WithAutomaticReconnect()`** — спасает от разрыва TCP в живом
  процессе, но не от гибели процесса при ребуте. Ортогонально этой фиче.

## Решение

Новый режим запуска `agent.exe --resume <statePath>` (по аналогии с существующим
`--revert <statePath>`), поднимаемый scheduled task с триггером `-AtStartup` под
SYSTEM. Механизм автозапуска ставится в `Open` и снимается в `Revert`.

### Почему scheduled task, а не Windows Service

Агент сейчас — интерактивная консоль (`Console.ReadLine` для СЗ, клавиши `C`/`Q`,
`ConsoleCloseGuard` на крестик, цветной Spectre-вывод). Служба потребовала бы
переписать весь жизненный цикл под `ServiceBase`/worker без консоли. Главный выигрыш
службы (авторестарт при **краше процесса**) вторичен — боль была именно **ребут
машины**, а не падение агента. Scheduled task `-AtStartup` переиспользует ровно тот
механизм, что уже есть для sshd/watchdog (`Register-ScheduledTask` ↔
`Unregister-ScheduledTask`), и ложится в `RevertState` одним флагом.

## Компоненты и изменения

### 1. `RevertState` (+2 поля)

```csharp
public string AutostartTaskName { get; set; } = "";   // szdiag-autostart-<СЗ>
public bool CreatedAutostartTask { get; set; }
```

### 2. `WindowsSystemAccessManager.Open` — новый шаг 8 (после watchdog)

Регистрирует автостарт-таск после того, как всё остальное поднято:

```
$a = New-ScheduledTaskAction -Execute '<exe>' -Argument '--resume "<statePath>"'
$t = New-ScheduledTaskTrigger -AtStartup
Register-ScheduledTask -TaskName 'szdiag-autostart-<СЗ>' -Action $a -Trigger $t `
    -RunLevel Highest -User 'SYSTEM' -Force
```

`state.AutostartTaskName` инициализируется в начале `Open` (рядом с прочими именами),
`state.CreatedAutostartTask = true; Persist();` — после регистрации.

**Guard от грязного старта.** В начале `Open`, если `state.json` уже существует и его
`Sz` **отличается** от текущего, — сначала `Revert(старый state)` (снимет в т.ч.
старый `szdiag-autostart-<oldSz>`), затем открывать новую сессию. Иначе автостарт-таск
прошлой незакрытой СЗ повиснет = след.

### 3. `WindowsSystemAccessManager.Resume(RevertState state)` — новый метод

Переподнимает **только то, что умерло от ребута**, не трогая живое (user / firewall /
token policy остаются как есть):

1. `_sshd.Start(port, keyLine, state.SshdTaskName)` — переподнять портативный sshd
   (метод уже идемпотентен: сносит остаток и стартует заново).
2. Пересоздать watchdog-таск с новым временем срабатывания `-At = now + WatchdogTimeout`
   (тот же `state.WatchdogTaskName`, `Register-ScheduledTask ... -Force`). **Сдвиг
   дедлайна:** серия ребутов под стрессом продлевает сессию — watchdog остаётся
   защитой «забыли закрыть», а хост всегда закроет вручную командой `close`. Исходный
   (несдвинутый) `-Once At <time>` из прошлой загрузки мог бы протухнуть и сработать
   сразу после ребута, грохнув только что поднятую сессию.

Порт, pub-key и timeout берутся из `appsettings.json` (`AgentOptions`) + `state.Sz`;
pub-key читается из `service_key.pub` рядом с exe (`ServicePublicKeyPath`).

`Resume` **не** пишет `state.json` заново — флаги и имена уже сохранены исходным `Open`.

### 4. `WindowsSystemAccessManager.Revert` — снятие автостарта первым шагом

Снять автостарт-таск **до всего остального**: если revert упадёт на середине, агент
не должен воскреснуть при следующем ребуте.

```csharp
if (state.CreatedAutostartTask)
    _ps.Run($"Unregister-ScheduledTask -TaskName '{state.AutostartTaskName}' " +
            "-Confirm:$false -ErrorAction SilentlyContinue", throwOnError: false);
```

Идемпотентно (под флагом), безопасно при повторном вызове и при watchdog-ревёрте.

### 5. `AgentSession.ResumeAsync(RevertState loaded)` — новый метод

Как `StartAsync`, но:
- `_state = loaded` (state пришёл с диска, не создан `Open`);
- `_manager.Resume(_state)` вместо `_manager.Open(_spec)`;
- дальше идентично: `OnRevert` → `ConnectAsync` → `RegisterAsync` под тем же СЗ.

Требует `ISystemAccessManager.Resume(RevertState)` в интерфейсе.

### 6. `Program.cs` — ветка `--resume` (headless)

Рядом с существующей веткой `--revert`, до интерактивного потока:

- читает `RevertStateStore.Load(statePath)`; если `null` — выход 0 (возобновлять
  нечего);
- биндит `AgentOptions` из `appsettings.json`;
- пересобирает `AccessSpec` из opts + `state.Sz` + pub-key из файла;
- поднимает `manager` / `sshd` / `link` / `AgentSession`;
- `await session.ResumeAsync(state)`;
- регистрирует обработчики `OnRunTests` / `OnRunDiag` (та же логика, что в
  интерактивной ветке — вынести в общий метод, чтобы не дублировать);
- фоновый heartbeat-цикл;
- **нет** `Console.ReadLine`, `ConsoleCloseGuard`, клавиш `C`/`Q` — окна нет, машина
  может стоять на lock screen;
- живёт до команды `Revert` от hub (тогда `session.RevertAsync()` + выход);
- весь вывод — только в `agent.log` (`AgentLog` уже пишет в файл).

Общий код обработчиков теста/диагностики и heartbeat-цикла вынести из top-level в
тестируемый метод/класс, чтобы `--resume` и интерактивная ветка его переиспользовали.

## Поток данных

```
[Интерактивный старт]  agent.exe
  → ReadLine СЗ → Open(spec)
      ├─ ... (sshd, watchdog, user, firewall, token) ...
      └─ шаг 8: Register autostart-task (-AtStartup, --resume)
  → Connect/Register → heartbeat → ждём C/Q/close

[Ребут машины]  → все процессы мертвы; autostart-task жив (-AtStartup)

[После ребута]  autostart-task → agent.exe --resume <state.json>
  → Load(state) → Resume(state)
      ├─ sshd.Start(state.SshdTaskName)      (переподнять единственно мёртвое)
      └─ watchdog reschedule (now + timeout) (-Force)
  → Connect/Register(state.Sz) → heartbeat → ждём close  (headless)

[Закрытие СЗ]  hub → Revert  (или watchdog, или C/крестик в интерактивном)
  → Unregister autostart-task  (ПЕРВЫМ)
  → ... остальной откат по флагам в обратном порядке ...
  → Delete(state.json)
```

## Обработка ошибок

- `--resume` без валидного `state.json` → тихий выход 0 (нечего возобновлять).
- `Resume` падает на `sshd.Start` → пробрасываем `SshdStartException` в лог; таск
  отработает при следующем ребуте (state на месте). Ручной старт агента остаётся
  фолбэком.
- `Unregister` автостарта в `Revert` — `-ErrorAction SilentlyContinue`, под флагом:
  повторный/watchdog-ревёрт безопасен.
- Guard грязного старта: остаточный `state.json` с другим `Sz` откатывается перед
  новым `Open`.

## Тестирование (TDD)

- `RevertStateStoreTests` — round-trip новых полей (`AutostartTaskName`,
  `CreatedAutostartTask`).
- `WindowsSystemAccessManagerTests`:
  - `Open` регистрирует автостарт-таск (mock `IPowerShellRunner`: вызов
    `Register-ScheduledTask` с `-AtStartup` и именем `szdiag-autostart-<СЗ>`);
  - `Revert` вызывает `Unregister-ScheduledTask` автостарта, причём **первым**;
  - `Resume` вызывает `sshd.Start` и пересоздаёт watchdog, но **не** трогает
    user/firewall/token policy;
  - guard: `Open` поверх остаточного state с другим `Sz` сначала откатывает старый.
- `AgentSessionTests` — `ResumeAsync` вызывает `manager.Resume` + `link.Connect`/
  `Register`, но не `manager.Open`.
- Логику `--resume` из top-level `Program.cs` вынести в тестируемый метод/класс.

## Осознанно вне scope (YAGNI / отдельные направления)

- **boot-time в heartbeat** («реальный вырубон vs лаг heartbeat») — отдельное
  открытое направление, не сюда.
- **Windows Service** — отклонена в пользу scheduled task.
- **Авторестарт при краше процесса агента** (без ребута) — служебная фича, не боль дня.

## Затрагиваемые файлы

- `src/SzDiag.Agent/RevertState.cs` — +2 поля.
- `src/SzDiag.Agent/WindowsSystemAccessManager.cs` — `Open` (+шаг 8, guard), `Revert`
  (+снятие автостарта), `Resume` (новый метод).
- `src/SzDiag.Agent/ISystemAccessManager.cs` — `Resume` в интерфейсе.
- `src/SzDiag.Agent/AgentSession.cs` — `ResumeAsync`.
- `src/SzDiag.Agent/Program.cs` — ветка `--resume`, вынос общих обработчиков.
- Зеркальные тесты в `tests/SzDiag.Agent.Tests/`.
