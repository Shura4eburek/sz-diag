# Bug Verification: второй экземпляр hub гасит туннель живого, Desk из исходников не видит hub

- **Slug**: hub-second-instance-tunnel
- **Tested**: 2026-10-19
- **Assessment**: ./assessment.md
- **Fix**: ./fix.md
- **Result**: verified

## Summary

Оба звена воспроизведены после фикса и не проявляются: второй hub на занятом порту выходит с кодом 3 до
старта служб (туннель не трогается, crit нет), первый продолжает отвечать; Desk из исходников видит hub
из dist и не запускает второй. Регрессий нет.

## Checks Performed

| Check | Command / Action | Result | Notes |
|-------|------------------|--------|-------|
| Репро: второй hub (post-fix) | собранный `SzDiag.Hub.exe` дважды на `http://127.0.0.1:45990`, туннель/бэкап выключены | pass | второй: exit 3, «hub на порту 45990 уже запущен — этот экземпляр не стартует.»; в логе нет crit и строк туннеля; первый — `/healthz` 200 |
| Блокировка освобождается | убить первый, запустить новый | pass | новый стартует (семафор уходит вместе с процессом) |
| Репро: Desk из исходников | `src\SzDiag.Desk\bin\Debug\net8.0\SzDiag.Desk.exe` при живом hub из dist | pass | лог: «старт, hub http://localhost:5099» → «hub уже работает», второй hub не запускался |
| Новые тесты | `dotnet test tests/SzDiag.Hub.Tests`, `tests/SzDiag.Desk.Tests` | pass | 293/293, 211/211 |
| Регрессия | `dotnet test` (солюшен) | pass* | всё зелёное, кроме `ScriptLintTests.AllRepoClientRecipes_NoFalseConcatWarning` — падал до фикса, файлы не затронуты |

## Output Excerpts

```
first alive: True; healthz: 200
second exited: True code: 3
first still alive: True; healthz: 200
after first killed, new one starts: True
[hub] hub на порту 45990 уже запущен — этот экземпляр не стартует.
16:04:56 старт, hub http://localhost:5099
16:04:58 hub уже работает
```

## Residual Risks

- Живой hub на боксе (5099) — ещё старая сборка из dist без блокировки; защита заработает после
  пересборки `build-dist` и перезапуска hub.
- Блокировка `Local\` — в пределах сеанса; hub из другого сеанса (служба) её не увидит.
- Туннель с реальным cloudflared в репро выключен: что второй экземпляр не доходит до `HubTunnelService`,
  доказано выходом до `app.Run` и отсутствием строк туннеля в его логе.

## Recommendation

Закрыть баг — проверено воспроизведением обоих звеньев. Пересобрать dist и перезапустить hub, чтобы
защита попала на бокс.
