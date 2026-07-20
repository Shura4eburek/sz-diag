# Быстрый старт: план Б — портативный sshd под SYSTEM

> Это handoff для новой сессии Claude (возможно, с другого компа). Прочитай и сразу
> берись за задачу — контекст ниже самодостаточный. Общий обзор проекта — в
> [../CLAUDE.md](../CLAUDE.md) и [vision.md](vision.md).

## Задача одним предложением

Поднимать портативный sshd на клиенте **под SYSTEM** (транзиентной scheduled task), а не
дочерним процессом админ-агента — иначе SSH-доступ не работает.

## Почему (диагноз, подтверждён вживую)

Сейчас `PortableSshServer.Start` форкает `sshd.exe` как дочерний процесс агента. Агент
админ, но **не SYSTEM**, а для создания logon-token при publickey-аутентификации нужен
`SeTcbPrivilege` (есть только у LocalSystem). Итог: sshd слушает, KEX проходит, но сессия
рвётся в самом начале userauth — `Connection reset`.

Подтверждено 2026-07-20 на СЗ 159873 (машина онлайн, `192.168.94.124`): `ssh -vv` рвётся
сразу после `SSH2_MSG_SERVICE_ACCEPT`, до отправки ключа. Это не про ключ (иначе было бы
`Permission denied (publickey)`), а про token-privilege. Симптом в логе клиента
`C:\ProgramData\szdiag\ssh\sshd.log` — `unable to create logon token`.

Как воспроизвести с хоста (при онлайн-СЗ):
```powershell
ssh -i secrets\svc_diag_key -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL svc-diag@<IP> "whoami"
```
Вернёт `<машина>\svc-diag` — token-privilege ОК; `Connection reset` — сработал риск.

## Подход (план Б)

Запускать sshd не через `Process.Start`, а транзиентной scheduled task под SYSTEM
(`Register-ScheduledTask ... -User 'SYSTEM' -RunLevel Highest`). Привязать к сессии,
чистить тем же watchdog-механизмом.

**Хорошая новость:** механизм регистрации под SYSTEM уже есть в проекте —
`WindowsSystemAccessManager.Open` регает watchdog-задачу ровно так
(`Register-ScheduledTask -User 'SYSTEM'`, см. её код). Тот же паттерн переиспользуется для
sshd.

## Где править (файлы)

- `src/SzDiag.Agent/PortableSshServer.cs` — `Start`/`Stop`. Сейчас `Process.Start(sshd.exe)`;
  заменить на запуск через SYSTEM-задачу (или вынести в отдельный `SystemSshdLauncher`).
  Учесть: как дождаться, что sshd поднялся (сейчас `WaitForExit(1500)` по дочернему
  процессу — под задачей процесс уже не дочерний, нужен другой сигнал: проверка порта/лога).
- `src/SzDiag.Agent/WindowsSystemAccessManager.cs` — `Open` зовёт `_sshd.Start(...)` (шаг 6);
  `Revert` зовёт `_sshd.Stop()`. Образец регистрации/снятия SYSTEM-задачи — там же (watchdog).
- `src/SzDiag.Agent/RevertState.cs` — добавить флаг под новый шаг (напр. `CreatedSshdTask`).

## Ключевой инвариант (не нарушать!)

Каждый шаг в `Open` **обязан** иметь парную ветку отката в `Revert` под своим флагом в
`RevertState` — иначе на клиентской машине останутся следы (это least-trusted устройство,
доступ откатывается без следов). Подробнее — раздел «Жизненный цикл доступа» в CLAUDE.md.

## Как начинать

Через superpowers-воркфлоу проекта: **brainstorming → спека → writing-plans →
subagent-driven execution**. Детали риска и фолбэка уже записаны в
[TESTING.md](TESTING.md) (раздел «Проверка token-privilege», строки ~205-213).

**Статус на 2026-07-20:** брейншторм начат, но не доведён до спеки. Спеки/плана ещё нет.
Начинать с brainstorming. Зависимая хотелка юзера — после рабочего SSH сделать короткий
runbook «дал СЗ → сразу коннект и диагностика, без ковыряния связи».
