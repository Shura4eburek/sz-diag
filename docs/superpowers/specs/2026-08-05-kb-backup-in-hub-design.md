# Бэкап базы знаний внутри hub

**Дата:** 2026-08-05
**Статус:** дизайн утверждён, реализации нет

## Проблема

Оффсайт-бэкап vault'а сейчас живёт в `tools/kb-backup.ps1` + задача планировщика
`SzDiag-KbBackup` (каждые 15 минут + при логоне). Работает, но:

- Логика бэкапа лежит вне продукта: отдельный PowerShell-скрипт, отдельная точка
  установки, отдельный лог (`dist\host\kb-backup.log`), свои грабли PS 5.1.
- Планировщик — лишний слой. Задача ставится вручную, живёт под конкретным профилем,
  и её состояние никак не видно из hub.
- Vault меняет в основном сам hub (`KnowledgeBaseScaffolder`, `KbReportStore`).
  Таймер, который тикает независимо от hub, решает задачу не там, где она возникает.

## Решение

Перенести бэкап в hub фоновым сервисом. Планировщик снимается, скрипт остаётся
ручной кнопкой.

### Компоненты

**`SzDiag.Kb/KbGitBackup.cs`** — вся логика, без знания о хостинге.

```
Task<KbBackupResult> RunAsync(CancellationToken ct)
```

Шаги: `git add -A` → `git status --porcelain` → пусто? вернуть `NoChanges` →
записать сообщение коммита во временный файл → `git commit -F <файл>` → `git push`.

Результат — `KbBackupResult` с исходом и текстом:

| Исход | Когда |
|---|---|
| `NoChanges` | vault не менялся, коммита нет |
| `Pushed` | закоммичено и выгружено, `ChangedFiles` = число строк `--porcelain` |
| `CommittedNotPushed` | коммит лёг локально, push не прошёл (сеть/креды) |
| `Failed` | не прошёл `add`/`commit`, либо vault не git-репозиторий |

**`SzDiag.Hub/KbBackupService.cs`** — `BackgroundService` по образцу `OfflineSweeper`:

1. Прогон **на старте** — догнать правки, сделанные руками в Obsidian, пока hub не был поднят.
2. Дальше `PeriodicTimer` на `Interval`.
3. Финальный прогон в `StopAsync` — Ctrl+C / крестик окна выгружают свежак сразу.

Регистрация в `Program.cs` рядом с `AddHostedService<OfflineSweeper>()`.

### Конфигурация

Новая секция в `HubOptions` (путь к vault не дублируем — берём существующий
`KnowledgeBaseRoot`):

```csharp
public KbBackupOptions KbBackup { get; set; } = new();

public sealed class KbBackupOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);
    public string Remote { get; set; } = "origin";
    public string Branch { get; set; } = "main";
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
```

`Enabled: false` — сервис не регистрирует таймер и молча выходит (рубильник для
машин, где vault не под git).

### Ключевые решения

**`git.exe` через `Process`, а не LibGit2Sharp.** Git на сервисном боксе уже есть,
креды GitHub берутся из Windows Credential Manager текущего профиля сами.
LibGit2Sharp в self-contained single-file publish тащит нативные библиотеки и требует
кормить креды руками. Цена shell-out — разбор текста, но разбирать надо ровно
`git status --porcelain` (пусто / не пусто + счёт строк).

**Сообщение коммита — через временный файл в UTF-8 без BOM.** Формат:
`kb: автосохранение <yyyy-MM-dd HH:mm> (<n> файл(ов))`. Писать через
`new UTF8Encoding(false)`; BOM утекает в первую строку заголовка коммита.
Это перенос грабли из скрипта, а не новая осторожность.

**Таймаут на каждый git-процесс.** Виснет сеть — процесс убиваем по
`CommandTimeout`, исход `Failed`/`CommittedNotPushed`. Фоновый бэкап не имеет права
держать hub при остановке.

**Исключения не выпускаем наружу.** Unhandled из `BackgroundService` валит хост
целиком (.NET 6+). Всё ловим внутри, наружу отдаём `Failed` и логируем.

**pre-commit hook в vault не трогаем.** Не пустил жирный файл — `commit` вернёт
ненулевой код, логируем причину (stderr hook'а), пробуем на следующем тике.

### Логи

Обычный `ILogger` хаба (пишет в консоль под липкой панелью). Отдельный
`kb-backup.log` уходит.

| Исход | Уровень | Текст |
|---|---|---|
| `NoChanges` | Debug | тихо, консоль не засоряем |
| `Pushed` | Information | `kb: выгружено N файл(ов)` |
| `CommittedNotPushed` | Warning | `kb: закоммичено локально, push не прошёл: <причина>` |
| `Failed` | Warning | `kb: бэкап не прошёл: <причина>` |

### Тесты

`tests/SzDiag.Kb.Tests` — `KbGitBackup` на временном репозитории с локальным
bare-remote (`git init --bare` в temp, добавлен как `origin`), сеть не нужна:

- vault без изменений → `NoChanges`, новых коммитов в remote нет;
- новый файл → `Pushed`, файл виден в remote, `ChangedFiles` = 1;
- удаление файла → `Pushed` (`add -A` ловит удаления);
- кириллица в сообщении коммита не ломается, первая строка без BOM;
- недостижимый remote → `CommittedNotPushed`, коммит лежит локально;
- каталог не git-репозиторий → `Failed`, исключения наружу нет.

`tests/SzDiag.Hub.Tests` — `KbBackupService`: при `Enabled: false` бэкап не
вызывается ни разу; исключение из `RunAsync` не роняет сервис.

### Миграция

1. Снять задачу планировщика: `.\tools\kb-backup.ps1 -Uninstall`.
2. `tools/kb-backup.ps1` остаётся как ручная кнопка «выгрузить сейчас» на случай,
   когда hub не поднят. Из скрипта убираются ветки `-Install`/`-IntervalMinutes`
   (штатное расписание теперь в hub); `-Uninstall` остаётся, чтобы снести задачу
   на машинах, где она уже стоит.
3. Скрипт сейчас не закоммичен (untracked) — закоммитить вместе с изменениями.

## Явно вне scope

- Второй провайдер бэкапа (Google Drive и т.п.).
- `FileSystemWatcher` с debounce вместо таймера — периода в 15 минут достаточно.
- Статус бэкапа в липкой панели hub — логов хватает.
- Чистка тестового мусора в vault (`СЗ/--help`, `СЗ/111111`, `СЗ/123123`) — отдельная задача.

## Известное ограничение

Правки в vault, сделанные при выключенном hub, лежат незакоммиченными до следующего
старта hub. Осознанно принято: hub поднят рабочий день, окно небольшое.
