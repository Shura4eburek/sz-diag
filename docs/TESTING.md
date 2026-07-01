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
.\tools\build-dist.ps1 -HubIp 192.168.1.50
```
Скрипт публикует exe, генерит SSH-ключ (`secrets\svc_diag_key`), пишет конфиги и
лаунчеры. Результат:
- `dist\host\`  — `start-hub.cmd`, `szcli.cmd`, `hub\`, `cli\`
- `dist\client\` — `SzDiag.Agent.exe` + `service_key.pub` + `testsuite.json`

Нужен установленный **.NET 8 SDK** и OpenSSH client (`ssh-keygen`).

## 1. Хост: открыть порт и запустить hub

Порт (PowerShell **от админа**, один раз):
```powershell
New-NetFirewallRule -DisplayName "szdiag-hub-5099" -Direction Inbound -Protocol TCP -LocalPort 5099 -Action Allow
```
Hub — двойной клик по `dist\host\start-hub.cmd`. Ждём `Now listening on: http://0.0.0.0:5099`.

## 2. Клиент: запустить агента

Если клиент — отдельная ВМ: скопируй туда папку `dist\client` целиком.

Проверь связь с хостом (обычный PowerShell):
```powershell
Test-NetConnection <IP-хоста> -Port 5099   # нужно TcpTestSucceeded : True
```

Запусти агента (**от админа** — он меняет систему):
```powershell
cd <путь>\dist\client
.\SzDiag.Agent.exe
```
Введи номер СЗ, напр. `156864`. Ждём `доступ открыт ● online`.

## 3. Хост: диагностика через CLI

```powershell
cd dist\host
.\szcli list                # 156864 ● online
.\szcli target 156864       # ssh svc-diag@<ip>
.\szcli test run 156864     # в окне агента: «Прогон тестов…» → «Отчёт залит на hub»
```

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
| Агент завис на «Открываю доступ», ничего дальше | Старый билд дёргал медленный `Get-WindowsCapability -Online`. Пересобери — теперь при живом `sshd` он не лезет в Windows Update. |
| `New-NetFirewallRule: file already exists` | Остаток от прерванного запуска. Новый агент идемпотентен — просто запусти снова, подчистит сам. |
| CLI/агент: `connection refused` / 401 | Hub не слушает нужный порт или не открыт firewall; токены не совпали. Проверь `dist\host\hub\appsettings.json` (`Urls`, `Hub.*Token`) и правило firewall. |
| `Test-NetConnection ... False` | Клиент не в той сети. Собери с `-HubIp <правильный IP хоста>` (у хоста может быть несколько адресов: LAN/VPN). |
| Агент требует OpenSSH, а его нет и WU недоступен | Поставь один раз вручную: `Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0` (от админа), затем запусти агента. |

## Автотесты (без хоста/клиента)
```powershell
dotnet test    # вся логика, ~76 тестов
```
