# Ручной тест sz-diag (хост + клиент)

Полный e2e: hub + CLI на **хосте**, agent на **клиенте**. Для теста подойдёт одна
машина (хост=клиент) или хост + отдельная Windows-ВМ.

## 0. Сборка (на хосте)

```powershell
git clone https://github.com/Shura4eburek/sz-diag.git
cd sz-diag

# хост = клиент (одна машина):
.\tools\build-dist.ps1

# ИЛИ клиент — отдельная ВМ: укажи LAN-IP хоста
.\tools\build-dist.ps1 -HubIp <HUB_LAN_IP>
```
Скрипт публикует exe, генерит SSH-ключ (`secrets\svc_diag_key`), пишет конфиги и
лаунчеры. Результат:
- `dist\host\`  — `start-hub.cmd`, `szcli.cmd`, `hub\` (+ `hub\agent-dist\` — пакет для
  апдейтера), `cli\`
- `dist\client\` — `SzDiag.Updater.exe` (точка входа) + `SzDiag.Agent.exe` +
  `service_key.pub` + `testsuite.json` + `version.txt`

Нужен установленный **.NET 8 SDK** и OpenSSH client (`ssh-keygen`).

### Стресс-утилиты (опционально, для шагов `app`)

Тяжёлые бинарники (TM5 / OCCT / FurMark / 3DMark) в репо не лежат — их кладут в
`client-tools\<tool>\`, откуда `build-dist.ps1` копирует в `dist\client\tools\`.
Пути в `testsuite.json` — `tools\<tool>\...`. Если папки нет — соответствующий шаг
просто отметится «не найден exe» и прогон продолжится.

- **OCCT**: `client-tools\occt\` = `OCCTCmd.exe` + `schedule.json` (взять из
  `deploy\occt\`) + файл лицензии `*.oke`. Гонит Combined+Power, отдаёт HTML-отчёт
  артефактом. Подробно — `deploy\occt\README.md`.
- **3DMark**: `client-tools\3dmark\` = **полная установка** 3DMark (не только
  `3DMarkCmd.exe`, но и ассеты/DLC + `.3dmdef`). Шаг гонит Time Spy бесконечным
  циклом (`-l 0`) и снимает скрин под нагрузкой.

## 1. Хост: открыть порт и запустить hub

Порты (PowerShell **от админа**, один раз): TCP — hub/SignalR, UDP — автообнаружение
клиентом.
```powershell
New-NetFirewallRule -DisplayName "szdiag-hub-5099" -Direction Inbound -Protocol TCP -LocalPort 5099 -Action Allow
New-NetFirewallRule -DisplayName "szdiag-discovery-5098" -Direction Inbound -Protocol UDP -LocalPort 5098 -Action Allow
```
Hub — двойной клик по `dist\host\start-hub.cmd`. Ждём `Now listening on: http://0.0.0.0:5099`.

## 2. Клиент: запустить агента

Если клиент — отдельная ВМ: скопируй туда папку `dist\client` целиком.

Проверь связь с хостом (обычный PowerShell):
```powershell
Test-NetConnection <IP-хоста> -Port 5099   # нужно TcpTestSucceeded : True
```

Запусти **апдейтер** (**от админа** — агент меняет систему):
```powershell
cd <путь>\dist\client
.\SzDiag.Updater.exe
```
`SzDiag.Updater.exe` — точка входа: находит hub, сверяет `version.txt`, при
расхождении качает свежий пакет агента (`agent.exe` + ssh + ключ + testsuite, без
`appsettings.json`/`tools`) и запускает `SzDiag.Agent.exe`. Первый заезд: на клиент
достаточно закинуть `SzDiag.Updater.exe` + `appsettings.json` (с токеном) — остальное
подтянется само. Прямой запуск `SzDiag.Agent.exe` тоже работает (без самообновления).

Введи номер СЗ, напр. `156864`. Ждём `доступ открыт ● online`.

Если `HubUrl` в `dist\client\appsettings.json` пуст (значение по умолчанию) — агент сам
найдёт hub в локальной сети: `Ищу hub в сети…` → `Hub найден: http://<ip>:5099`. Работает
только в одном сетевом сегменте (broadcast); если хост и клиент разделены роутером/VPN —
укажи `HubUrl` вручную (`http://<IP-хоста>:5099`) или пересобери с `-HubIp <адрес>`.

## 3. Хост: диагностика через CLI

Столбец «Активность» в `list`/`watch` показывает, что идёт на машине: `Тест OCCT 5мин 44сек`
(время тикает), `готов · последний: TM5 ✓` в простое, `—` для offline.

```powershell
cd dist\host
.\szcli list                # 156864 ● online
.\szcli target 156864       # ssh svc-diag@<ip>
.\szcli test run 156864     # весь набор: «Прогон тестов…» → «Отчёт залит на hub»
.\szcli test run 156864 occt        # только OCCT
.\szcli test run 156864 tm5,furmark # подмножество шагов (id через запятую)
.\szcli diag run 156864             # read-only снапшот → diag.md (все секции)
.\szcli diag run 156864 reboots,whea,memory  # точечно (спонтанные ребуты)
```

`diag run` (read-only, без стресса) кладёт `reports\<ts>\diag.md` — структурированный
снапшот железа/дисков/SMART/событий/ребутов/WHEA. Один файл вместо россыпи ad-hoc ssh.

SSH-вход по ключу:
```powershell
ssh -i <корень репо>\secrets\svc_diag_key -o StrictHostKeyChecking=no svc-diag@<IP-клиента> "whoami; hostname"
```

Отчёт: `dist\host\kb\СЗ\156864\reports\<timestamp>\report.md` (+ `screen-1.png`).

## 4. Закрытие

В окне агента нажми `C` — откат. Проверка чистоты (на клиенте, админ):
```powershell
net user svc-diag                                  # «пользователь не найден»
Get-NetFirewallRule -DisplayName "szdiag-ssh-*"    # пусто
```

## Траблшутинг (грабли, которые уже ловили)

| Симптом | Причина / решение |
|---|---|
| `DirectoryNotFoundException: service_key.pub` | Старый билд с абсолютным путём. Пересобери `build-dist.ps1` (пути теперь относительные, рядом с exe). |
| Агент завис на «Открываю доступ», ничего дальше | Устарело: старый билд дёргал медленный `Add-WindowsCapability -Online` (Windows Update). Теперь агент носит свой портативный sshd (`dist\client\ssh`) и вообще не лезет в WU — пересобери. |
| `New-NetFirewallRule: file already exists` | Остаток от прерванного запуска. Новый агент идемпотентен — просто запусти снова, подчистит сам. |
| CLI/агент: `connection refused` / 401 | Hub не слушает нужный порт или не открыт firewall; токены не совпали. Проверь `dist\host\hub\appsettings.json` (`Urls`, `Hub.*Token`) и правило firewall. |
| `Test-NetConnection ... False` | Клиент не в той сети. Собери с `-HubIp <правильный IP хоста>` (у хоста может быть несколько адресов: LAN/VPN). |
| Агент: «hub не найден в сети» | Хост и клиент в разных сегментах (роутер/VPN/VLAN без broadcast) — автообнаружение не проходит. Укажи `HubUrl` вручную в `dist\client\appsettings.json` или пересобери с `-HubIp <IP-хоста>`. Проверь также, что открыт UDP-порт 5098 на хосте. |
| Агент упал на `Start-Service sshd` / битые host-ключи | Больше не воспроизводится: агент носит свой портативный sshd (`dist\client\ssh`) и генерит свежие host-ключи каждую сессию. Системная служба sshd не используется. |
| `Не удалось поднять SSH: sshd не стартовал: <причина>` | Портативный sshd упал сразу. Причина — последние строки его лога (`C:\ProgramData\szdiag\ssh\sshd.log`). Часто: sshd под админ-токеном не смог создать logon-token (см. e2e-раздел про SYSTEM). |
| `dist\client\ssh` пуст / нет sshd.exe | `build-dist.ps1` не скачал портативный OpenSSH (нет интернета на хосте при первой сборке). Положи распакованный OpenSSH-Win64 в `client-tools\ssh\` вручную и пересобери. |

## Headless-автоматизация по SSH: грабли и обходы

Управление агентом/тестами по SSH (без физического доступа к экрану клиента)
упирается в одно системное ограничение: **SSH-сессия не имеет своего Window
Station/Desktop**. Любой GUI-процесс, запущенный напрямую по SSH, либо падает
при попытке создать окно, либо виснет на диалоге, который некому кликнуть.

- **Интерактивное меню агента (`[C]`/`[Q]`)** — крашится с
  `InvalidOperationException: Cannot read keys when console input has been
  redirected`, если запустить `SzDiag.Agent.exe` через `ssh host "SzDiag.Agent.exe"`
  (даже с `echo <СЗ> |`). Номер СЗ вводится нормально, а вот `Console.ReadKey()`
  для меню — нет.
- **GUI-тулзы (SDI, TM5, инсталляторы)** — крашатся конкретно в `USER32.dll`
  (`0xC0000005`, access violation) при попытке создать окно без десктопа, либо
  просто виснут без вывода.
- **Обход**: дать процессу реальный десктоп через Task Scheduler с флагом
  `/it` (interactive token) — он подцепляет desktop уже залогиненной
  интерактивной сессии (`quser` показывает `Active` на `console`), без пароля
  того пользователя. Для админ-аккаунтов добавь `/rl highest`, иначе без
  явного consent-запроса поймаешь `ERROR_ELEVATION_REQUIRED` (740).
  ```powershell
  schtasks /create /tn TmpTask /tr '<exe или ps1>' /sc once /st 23:59 `
    /ru <ДОМЕН>\<ИмяПользователя> /it /rl highest /f
  schtasks /run /tn TmpTask
  # ... дождаться результата ...
  schtasks /delete /tn TmpTask /f   # удалить сразу после — не оставлять на клиенте
  ```
  Рабочая директория у `schtasks /tr` не резолвится сама — оборачивай в `.bat`
  с `cd /d <путь>` или указывай в самом `.ps1`.
- **UAC / кастомные admin-диалоги** — даже с рабочим десктопом настоящий UAC
  consent рисуется на **Secure Desktop**, отдельном от обычного: ни
  `EnumWindows`, ни `SendKeys`/`PostMessage` туда не достают в принципе, это
  осознанная защита Windows. Клик может сделать только живой человек за
  монитором. Отдельно от UAC — некоторые тулзы (TM5) показывают свой
  диалог-предупреждение «нужны права администратора для AWE» на обычном
  десктопе; его можно поймать через `EnumChildWindows` + `SendMessage(hBtn,
  BM_CLICK=0x00F5, …)` по хендлу нужной кнопки — надёжнее, чем `SendKeys`
  (тот требует реального фокуса окна, `SetForegroundWindow` из фона часто
  тихо не срабатывает). **Не кликай остальные кнопки того же окна на всякий
  случай** — так один раз случайно словили клик по «Settings» и сбили
  идущий тест TM5 до 0 процессов.
- **Сетевая шара, смонтированная в SSH-сессии, не видна из сессии `/it`** —
  `net use` с одними и теми же учётными данными успешно работает в plain-SSH
  сессии (`svc-diag`), но даёт `System error 67 (network name not found)` из
  интерактивной сессии, поднятой через `schtasks /it`, даже после `net use *
  /delete`. Обход: скачать/скопировать файлы локально через уже рабочую
  SSH-сессию (`robocopy` с шары на `C:\...`), а сам инсталлятор/exe запускать
  из локальной копии в `/it`-сессии.
- **Под экстремальной нагрузкой (OCCT Combined Extreme, AVX512, все ядра) SSH
  подвисает** — `Connection timed out` на обычном таймауте выглядит как
  зависание системы, но это просто нехватка планирования CPU для sshd.
  Проверяй с `-o ConnectTimeout=15..20` прежде чем делать вывод о реальном
  фризе; если и так не отвечает — тогда уже фриз.
- **`TestRunner.HasCompletionWindow` (см. `src/SzDiag.Agent/TestRunner.cs`) не
  проверяет `IsProcessAlive`** — если у шага задан `completionWindowClass» и
  ты убил процесс вручную (снаружи агента), раннер прождёт полный
  `durationSeconds`, ничего не заметив раньше — картины «зависшего теста»
  можно избежать, просто не убивая процессы мимо агента.
- **`SessionRegistry.SetActivity` (hub) не обновляет `Status`/`LastHeartbeat`**
  — если до этого статус словил `Offline` (например по `MarkStaleOffline` во
  время затыка с сетью), `list`/`watch` в CLI будет показывать `offline` даже
  у живого, активно работающего агента. Фикс уже в исходниках (`Status =
  SessionStatus.Online, LastHeartbeat = now` добавлены в `SetActivity`) — не
  забудь пересобрать `dist\host\hub` перед следующим e2e-прогоном.
- **Агент не переживает перезагрузку клиента и не переживает `Stop-Process`**
  — придётся вручную перезапускать (`schtasks /it` + номер СЗ) после каждого
  ребута; watchdog-таймер (`WatchdogHours`) отсчитывается заново с момента
  этого нового `Open()`.
- **3DMarkCmd.exe (CLI-эдишн) требует платную лицензию Professional/Developer**
  — без неё падает почти мгновенно с `Professional or Developer license key
  required for command line access`, даже если файлы разложены как в
  «portable»-копии. Нужна полная установка через официальный инсталлятор
  (ISO/Steam) с активной лицензией. После разового теста сносится через
  `<PackageCache>\...\3dmark-setup.exe /uninstall /S` (не просто `/S` —
  бандл требует явный `/uninstall`), и перед этим обязательно
  `Stop-Service "Futuremark SystemInfo Service"` — иначе удаление падает с
  кодом `1603` (файлы залочены работающей службой). Отдельный MSI-компонент
  `Futuremark SystemInfo` при этом не всегда сносится вместе с 3DMark — проверь
  `Get-ItemProperty HKLM:\...\Uninstall\*` и снеси `MsiExec.exe /X{guid} /qn`
  отдельно, если папка `Program Files (x86)\Futuremark` осталась.

## E2e портативного sshd (три сценария)

Проверять на реальной клиентской машине после `build-dist.ps1`:

1. **Чистая машина** (системного sshd нет вообще) — агент должен открыть доступ без
   Windows Update. Раньше тут висло на «Открываю доступ».
2. **Рабочий системный sshd** — агент гасит его на сессию (`StoppedSystemSshd`),
   поднимает свой, при закрытии СЗ системный возвращается (`Get-Service sshd` → Running).
3. **Битый системный sshd** — наличие битых системных host-ключей больше не влияет:
   агент их не трогает, использует свои.

**Проверка token-privilege (план Б, e2e пройден 2026-07-21).** sshd требует
SeTcbPrivilege для создания logon-token при publickey-логине — он есть только у
LocalSystem. Поэтому наш sshd поднимается **транзиентной scheduled task под SYSTEM**
(`szdiag-sshd-<СЗ>`), а не дочерним процессом админ-агента. Проверь: после «Открываю
доступ» подключись с хоста
`ssh -i secrets\svc_diag_key -o StrictHostKeyChecking=no svc-diag@<IP> "whoami"`.
- Ожидается `<машина>\svc-diag` — token-privilege ОК, план Б работает.
- `Connection reset` + в `sshd.log` (`C:\ProgramData\szdiag\ssh\sshd.log`) `unable to
  create logon token` — задача не под SYSTEM (проверь `Get-ScheduledTask
  szdiag-sshd-*` → Principal RunAs = SYSTEM) или sshd не поднялся (см. хвост лога).
- `sshd не стартовал: ... bad permissions ... no hostkeys available` — sshd под SYSTEM
  отвергает host-ключ: ssh-keygen оставляет явную ACE юзера-создателя (её `/inheritance:r`
  не чистит) и делает его владельцем файла. Пофикшено в `PortableSshServer`
  (`BuildHardenAclCommand`: `/setowner` Administrators + `/remove:g` создателя + гранты
  SYSTEM/Administrators). Если снова видишь — билд агента старый, пересобери.
- sshd не стартовал вообще → агент бросит `SshdStartException` с причиной из лога
  (поллинг порта не дождался за 5 c).

Проверка чистоты после закрытия СЗ (на клиенте, админ):
```powershell
net user svc-diag                                  # «пользователь не найден»
Get-NetFirewallRule -DisplayName "szdiag-ssh-*"    # пусто
Get-ScheduledTask szdiag-sshd-* -ErrorAction SilentlyContinue   # пусто (задача снята)
Get-CimInstance Win32_Process -Filter "Name='sshd.exe'"          # нет нашего sshd
Test-Path C:\ProgramData\szdiag\ssh                # False (host-ключи снесены)
```

## E2e апдейтера (самообновление клиента)

Точка входа на клиенте — `SzDiag.Updater.exe` (не агент напрямую). Проверять после
`build-dist.ps1` (он кладёт пакет в `dist\host\hub\agent-dist\`) при живом hub:

1. **Первый заезд**: в чистую папку — только `SzDiag.Updater.exe` + `appsettings.json`
   (с `AgentToken`, опц. `HubUrl`). Запуск от админа → `Hub: … → Обновление: (нет) ->
   <version> → Пакет применён` → появляются `SzDiag.Agent.exe`, `ssh\`, `service_key.pub`,
   `testsuite.json`, `version.txt` → запускается агент (спрашивает СЗ).
2. **Повторный запуск** без правок → `Версия актуальна.` → сразу агент, без скачивания.
3. **После правки агента** + `build-dist.ps1` → версии разошлись → скачивание → агент с
   правкой. Локальный `appsettings.json` **не** перезаписан.

Грабли: запуск НЕ от админа → `Агенту нужны права администратора…` (agent помечен
requireAdministrator). Старый hub без `/agent/*` (404) / битый sha256 → апдейтер запустит
локального агента, если он есть.

## Автотесты (без хоста/клиента)
```powershell
dotnet test    # вся логика, ~174 теста
```
