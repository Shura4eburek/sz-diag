# Tasks: Редизайн Desk на дизайн-систему Shirabe

**Input**: `specs/001-desk-shirabe-design/` (plan.md, spec.md, data-model.md, contracts/ui-contract.md, research.md)
**Tests**: ThemeTests (план) + обновление существующих smoke-тестов, где меняются строки.
Источник дизайна: артефакт https://claude.ai/artifact/TYJjNu1apzCZjcvczfoPxy (`project/tokens.css`,
`project/components/*/README.md`, `components/bundle.css` — значения размеров/состояний,
`components/bundle.js` — SVG-пути иконок). Пути ниже — от корня репозитория.

## Phase 1: Setup

- [X] T001 (Inter — пакетом Avalonia.Fonts.Inter: у TTF релиза веса заведены отдельными семействами) Скачать TTF Inter (Regular, Medium, SemiBold) и JetBrains Mono (Regular) из официальных релизов GitHub в `src/SzDiag.Desk/Assets/Fonts/`, положить `OFL-Inter.txt` и `OFL-JetBrainsMono.txt`
- [X] T002 Подключить шрифты как `AvaloniaResource Include="Assets\Fonts\**"` в `src/SzDiag.Desk/SzDiag.Desk.csproj`
- [X] T003 [P] Вытащить из артефакта `components/bundle.js` SVG-пути нужных иконок (bolt, snow/freeze, stop, chat, terminal, panel-right, min, max, restore, close, check, x, chevron, alert, cpu, folder) в рабочую заметку для T007

## Phase 2: Foundational (блокирует все истории)

- [X] T004 Переписать `src/SzDiag.Desk/Theme/Tokens.axaml` по data-model.md: все цвета `Bg.*`/`Border.*`/`Text.*`/`Accent.*`/`Status.*`/`Diff.*` (значения из таблицы «Цвета», `Status.Waiting` = #7aa5fb, `Status.Done` = #a0a4ab), `Space.*`, `Radius.*` (CornerRadius 3/4/6/8/10/999), `Size.*` (40/36/28/24/26/24/16), `Font.Ui` = `avares://SzDiag.Desk/Assets/Fonts#Inter`, `Font.Mono` = `…#JetBrains Mono`; старые ключи удалить
- [X] T005 В том же `src/SzDiag.Desk/Theme/Tokens.axaml` перекрыть Fluent-ресурсы контролов (ButtonBackground*, TextControl*, ListBoxItem*, TabItemHeader*, ToggleButton*, ToolTip*, ScrollBar*, ComboBox*, SystemControlFocusVisual*) токенами — hover `Bg.Hover`, pressed `Bg.Pressed`, рамки полей `Border.Control`/`Border.ControlHover`/`Border.Focus`
- [X] T006 [P] Создать `src/SzDiag.Desk/Theme/Typography.axaml`: классы `TextBlock`/`SelectableTextBlock` `.heading` 16/22/600, `.title` 14/20/600, `.body` 13/18/400, `.body-medium` 13/18/500, `.small` 12/16/400, `.caption` 11/14/500, `.code` 12/18 mono, `.code-small` 11/16 mono, `.secondary` (Text.Secondary); `FontFeatures="+tnum"` у body/small/code*
- [X] T007 [P] Создать `src/SzDiag.Desk/Theme/Icons.axaml`: `StreamGeometry` по ключам `Icon.<имя>` из T003 + стиль `Path.icon` (16×16, Stroke = Foreground родителя, StrokeThickness 1.2, StrokeLineCap/Join Round, Stretch None)
- [X] T008 Переписать `src/SzDiag.Desk/Theme/Controls.axaml` по контракту: `Window` (Font.Ui, 13, Text.Primary, Bg.Base); `Border.island` (Bg.Surface1, Radius.Island, без тени), `Border.card` (Bg.Surface2, Border.Default, Radius.Lg), `Border.code` (Bg.Code, Radius.Md); `Button` по умолчанию = secondary (высота 28, Radius.Sm, рамка Border.Control), `.primary` (Accent.Default/Hover/Pressed, Text.OnAccent), `.danger` (Bg.Danger), `.ghost`; `Button.caption`/`.caption.close` 46×40 без скругления; `ToggleButton.icon` (28×28, checked — Accent.Subtle + Accent.Text); `TextBox` 28, рамка, focus `Border.Focus`; `TextBox.error` (рамка Status.Error); `ListBoxItem` (Radius.Sm, MinHeight 26, selected Bg.Selected, selected без фокуса Bg.SelectedInactive); `TabItem` (36, body-medium, активная — подчёркивание 2px Accent.Default); `ProgressBar` (4px, Accent.Default на Bg.Surface2); `ToolTip` (Bg.Surface3, Border.Default, Radius.Md, small); `Ellipse.dot` 8×8 + `.running/.approval/.error/.waiting/.done`, у `.running.pulse` — анимация Opacity 1→0,4→1 за 1,6 с; disabled — Text.Disabled везде; удалить класс `caps`
- [X] T009 Подключить в `src/SzDiag.Desk/App.axaml` Typography/Icons/Controls, акцент палитры Fluent → `#2f6ae0`
- [X] T010 Перевести `src/SzDiag.Desk/Views/Converters.cs` на новые ключи по таблице «Состояния»: Liveness Online→Status.Running, LagSuspected→Status.Done, NoContact→Status.Approval, иначе Status.Error; Session Working→Status.Running, WaitingPermission→Status.Approval, AnsweringPeer→Status.Waiting, Crashed→Status.Error, иначе Status.Done; Transfer Done→Status.Running, Failed→Status.Error, иначе Accent.Default; WarnToBrush→Status.Approval; «⚠ откат не завершён» → «откат не завершён»
- [X] T011 Создать `tests/SzDiag.Desk.Tests/ThemeTests.cs`: (1) каждый ключ из data-model.md резолвится в `Application.Current`; (2) ни один `src/SzDiag.Desk/Views/**/*.axaml` не содержит литерала `#[0-9a-fA-F]{6,8}`; (3) `Font.Ui`/`Font.Mono` резолвятся в гарнитуры Inter/JetBrains Mono (Headless, `FontManager.TryGetGlyphTypeface`); (4) в `Views/**/*.axaml` нет эмодзи (⚡🧊💬■⚠)
- [X] T012 `dotnet build` + `dotnet test tests/SzDiag.Desk.Tests` — сборка проходит, ThemeTests (2) и (4) пока красные, остальные зелёные

**Checkpoint**: тема на месте — экраны можно переводить независимо.

## Phase 3: User Story 1 — рама, острова, тулбар, статус-бар (P1) 🎯 MVP

**Goal**: окно по карточке MainWindow. **Independent Test**: smoke-тесты MainWindow зелёные, визуальная сверка п.1–2 quickstart.

- [X] T013 [US1] Переделать `src/SzDiag.Desk/Views/MainWindow.axaml`: фон Bg.Base; тулбар Height 40 сверху, статус-бар 24 снизу (без рамок-линий, на раме); `Grid ColumnDefinitions="268,*,Auto"` с Margin 6 и зазором 6 между островами; `LeftPane`, `CenterPane` (обёртка Border.island), `InspectorPane` Width 308 — `Border.island`; `ExtendClientAreaTitleBarHeightHint="40"`; все x:Name по ui-contract.md сохранить
- [X] T014 [US1] Переделать `src/SzDiag.Desk/Views/TitleBar.axaml`: Height 40, фон Bg.Base, слева иконка Desk (Path.icon, Text.Secondary) + «SzDiag» body-medium; кнопки `Button.caption` 46×40 с `Path.icon` min/max/close
- [X] T015 [US1] В `src/SzDiag.Desk/Views/TitleBar.axaml.cs`: двойной клик по `DragArea` — развернуть/восстановить; при `WindowState.Maximized` иконка `MaxButton` → `Icon.Restore`, подсказка «Восстановить»
- [X] T016 [US1] Ширина < 900+308: при нехватке места инспектор скрывается (не сжимает чат) — `src/SzDiag.Desk/Views/MainWindow.axaml.cs`, без изменения `IsInspectorOpen` в модели
- [X] T017 [US1] Прогнать `MainWindowSmokeTests`, поправить только ожидания, завязанные на вид (не на поведение), в `tests/SzDiag.Desk.Tests/MainWindowSmokeTests.cs`

## Phase 4: User Story 2 — список заявок и архив (P1)

**Goal**: левый остров по компонентам Tree/StatusDot/LimitBadge. **Independent Test**: снапшот с СЗ всех состояний.

- [X] T018 [US2] `src/SzDiag.Desk/Views/SzListView.axaml`: заголовки `.caption`/Text.Secondary «Заявки · {0}» и «Без агента · архив» (регистр предложения); строки: `Ellipse.dot` живости, номер `.body-medium`, подпись и железо `.small.secondary` с обрезкой и полным текстом в ToolTip; бейджи — Border Radius.Xs, Bg.Surface2, `.caption`: ⚡N → `Path.icon` Icon.Bolt + число Status.Error; 🧊 → Icon.Snow Accent.Text (x:Name `FrozenBadge` сохранить); ≈СЗ → Accent.Text; 💬СЗ → Icon.Chat Accent.Text; точка сессии `Ellipse.dot` + `.pulse` при Working; удалить локальные стили ListBoxItem (берутся из темы)
- [X] T019 [US2] Обновить тесты, сверяющие заголовки «ЗАЯВКИ»/«АРХИВ», в `tests/SzDiag.Desk.Tests/` (grep по строкам)

## Phase 5: User Story 3 — чат (P1)

**Goal**: шапка, лента, Composer по карточкам ChatMessage/ToolCall/ApprovalCard/QuestionCard/Composer. **Independent Test**: сессия со всеми типами элементов ленты.

- [X] T020 [US3] Шапка чата в `src/SzDiag.Desk/Views/MainWindow.axaml`: высота 40, низ Border.Subtle; `Title` `.title`; профиль — бейдж `.caption` на Bg.Surface2; состояние — `Ellipse.dot` + слово `.small`; `TerminalButton` → Button с Icon.Terminal + «Открыть в терминале»; `InspectorToggle` → `ToggleButton.icon` с Icon.PanelRight
- [X] T021 [US3] `src/SzDiag.Desk/Views/ChatView.axaml`, лента: реплика оператора — Border Bg.Surface2, Radius.Lg, Padding 12,8, Text.Primary (вместо Accent); ответ Claude — Markdown со стилями body/code (Bg.Code для блоков, Font.Mono); ToolFeedItem — `Border.card` с заголовком `.code` (имя Text.Primary, аргументы Text.Secondary), статус вызова точкой, раскрытое тело — Bg.Code
- [X] T022 [US3] `src/SzDiag.Desk/Views/ChatView.axaml`: карточки разрешения/вопроса — `Border.card` с левой полосой 2px Status.Approval, заголовок «Ждёт подтверждения» `.title`, кнопки «Разрешить» `.primary`, «Запретить» secondary, прочие ghost; все Command/привязки — без изменений
- [X] T023 [US3] `src/SzDiag.Desk/Views/ChatView.axaml`, Composer: Border Bg.Surface2 + Border.Control (focus-within Border.Focus), Radius.Lg; «Отправить» `.primary`; «■» → Button с Icon.Stop, подсказка прежняя; строка очереди `.small.secondary`
- [X] T024 [US3] Стили Markdown.Avalonia (заголовки heading/title, код Font.Mono на Bg.Code, ссылки Accent.Text) — в `src/SzDiag.Desk/Theme/Controls.axaml` (или `Theme/Markdown.axaml`, подключить в App.axaml)
- [X] T025 [US3] Прогнать `ChatWindowSmokeTests`, `MainViewModelChatTests` в `tests/SzDiag.Desk.Tests/` — зелёные

## Phase 6: User Story 4 — инспектор (P2)

**Goal**: EditorTabs + содержимое на токенах. **Independent Test**: все 7 вкладок с данными и без.

- [X] T026 [US4] `src/SzDiag.Desk/Views/InspectorView.axaml`: `InspectorTabs` — вкладки по стилю TabItem темы (36, подчёркивание), низ Border.Subtle; убрать локальные цвета; подписи `.small.secondary`, значения `.body`, числа `.code-small`
- [X] T027 [P] [US4] `src/SzDiag.Desk/Views/Inspector/RebootsTabView.axaml`, `JobsTabView.axaml`, `SensorsTabView.axaml`, `JournalTabView.axaml`, `HardwareTabView.axaml`: строки 26, моно для времени/чисел, пустые состояния — иконка + одна строка `.secondary` по центру (EmptyStates)
- [X] T028 [US4] `src/SzDiag.Desk/Views/Inspector/ActionsView.axaml`: обычные действия — secondary, разрушительные (close/revert/force) — `.danger` с явной подписью; литералы цвета убрать
- [X] T029 [US4] Прогнать `InspectorSmokeTests` в `tests/SzDiag.Desk.Tests/`

## Phase 7: User Story 5 — передачи, статус-бар, пустые состояния (P2)

**Goal**: нижняя tool window, StatusBar, EmptyStates. **Independent Test**: pull, hub выключен, пустой центр.

- [X] T030 [P] [US5] `src/SzDiag.Desk/Views/TransfersView.axaml`: нижняя панель внутри центрального острова, верх Border.Subtle, заголовок «Передачи» `.caption.secondary`, строка — имя `.body`, ProgressBar темы, проценты/размер `.code-small`, статус словом + точкой
- [X] T031 [P] [US5] `src/SzDiag.Desk/Views/StatusBarView.axaml`: 24, фон Bg.Base, `.small` Text.Secondary, разделители Border.Subtle, состояние hub — `Ellipse.dot` + слово (ошибка — Status.Error), «данные на ЧЧ:ММ» сохраняется
- [X] T032 [US5] Пустые состояния центра в `src/SzDiag.Desk/Views/MainWindow.axaml` (`OfflineSessionPanel`, `StartSessionPanel`): иконка 16 Text.Secondary, `.title` «Выберите заявку слева», `.secondary` пояснение; `OfflineSzBox` — TextBox 28, Width 230; класс `.error` при `OfflineSzError` ≠ null, текст ошибки `.small` Status.Error (вместо `#ff6b61`); кнопки профилей — первая `.primary`, остальные secondary

## Phase 8: Polish

- [X] T033 `dotnet test` (весь солюшен) — ThemeTests (1)–(4) зелёные, остальные без регресса; `grep -rn "#[0-9a-fA-F]\{6\}" src/SzDiag.Desk/Views` → 0
- [X] T034 Ручная сверка по `specs/001-desk-shirabe-design/quickstart.md` (`dotnet run --project src/SzDiag.Desk`), скриншот окна рядом с карточкой MainWindow; 900×520 и масштаб 150%
- [X] T035 [P] Обновить описание Desk в `CLAUDE.md` и `docs/dev-knowledge-base.md` (тема Shirabe, где токены, правило «цвета только из Tokens.axaml»); комментарий в `Theme/Tokens.axaml` — ссылка на артефакт вместо «макета B v2»

## Dependencies

- Phase 1 → Phase 2 → US1 → (US2, US3, US4, US5 — независимы, но правят общий `MainWindow.axaml`: US1 → US3 → US5 по этому файлу последовательно) → Polish.
- T004 до T005/T008/T010; T003 до T007; T006–T008 до любых экранов.

## Parallel

- Phase 2: T006 ∥ T007 после T004.
- После US1: US2 (SzListView), US4 (Inspector/*), T030 ∥ T031 — разные файлы.
- US4: T027 — пять файлов вкладок параллельно.

## Implementation Strategy

MVP — Phase 1–3 (тема + каркас окна): уже даёт язык Shirabe и проверяемо глазами. Дальше
список → чат → инспектор → передачи/статус-бар, после каждой истории — `dotnet test` Desk.
Выполнение inline, без субагентов.

## Phase 9: Convergence

- [X] T036 Убрать «⚡» из заметок ленты о смене boot в `src/SzDiag.Desk/ViewModels/MainViewModel.cs` (строки «был ребут или вырубон (⚡N)» → «… (вырубонов: N)») и ожидание в `tests/SzDiag.Desk.Tests/MainViewModelChatTests.cs` per FR-005/SC-005 (partial)
- [X] T037 Числа статус-бара по тону Shirabe в `src/SzDiag.Desk/ViewModels/StatusBarViewModel.cs` `FormatTokens`: десятичная запятая и строчные суффиксы (`184,2k`, `1,2M`, `$2,84`), ожидания — в `tests/SzDiag.Desk.Tests/StatusBarViewModelTests.cs` per FR-011 (partial)
- [X] T038 Длительность ToolCall по тону Shirabe в `src/SzDiag.Desk/ViewModels/FeedItems.cs` `FormatDuration`: `12 с`, `1:05` вместо `12с`, `1м 05с`; ожидания — в тестах Desk per FR-011 (partial)
