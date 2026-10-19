# Data Model: токены Shirabe в ресурсах Desk

Сущность одна — **набор токенов** (ресурсы `Theme/Tokens.axaml`). Имя токена Shirabe
`group.name-part` → ключ ресурса `Group.NamePart` (точка остаётся, дефисы → PascalCase).

## Цвета (тёмная тема, значения из tokens.css)

| Токен | Ключ | Значение |
|---|---|---|
| bg.base | Bg.Base | #131417 |
| bg.surface-1 | Bg.Surface1 | #1e1f22 |
| bg.surface-2 | Bg.Surface2 | #2b2d30 |
| bg.surface-3 | Bg.Surface3 | #35373c |
| bg.code | Bg.Code | #191a1c |
| bg.hover | Bg.Hover | #313338 |
| bg.pressed | Bg.Pressed | #3a3c41 |
| bg.selected | Bg.Selected | #2e436e |
| bg.selected-inactive | Bg.SelectedInactive | #36383d |
| bg.disabled | Bg.Disabled | #25262a |
| bg.danger | Bg.Danger | #c63d36 |
| border.subtle / default / control / control-hover / focus | Border.* | #2b2d30 / #3e4045 / #7d8189 / #9a9ea6 / #4b86f5 |
| text.primary / secondary / disabled / on-accent | Text.* | #dfe1e5 / #a0a4ab / #5c6067 / #ffffff |
| accent.default / hover / pressed / text / subtle | Accent.* | #2f6ae0 / #2a63d6 / #2354b8 / #7aa5fb / #233250 |
| status.running / approval / error | Status.* | #62bb68 / #e3a53c / #f06a62 |
| status.waiting | Status.Waiting | = Accent.Text |
| status.done | Status.Done | = Text.Secondary |
| status.success / warning | Status.* | = Running / Approval |
| diff.add-bg / del-bg | Diff.* | #203626 / #3d2427 |

## Размеры

`Space.*` 2/4/6/8/12/16/20/24 · `Radius.Xs/Sm/Md/Lg/Island/Full` 3/4/6/8/10/999 ·
`Size.Toolbar` 40 · `Size.Tab` 36 · `Size.Control` 28 · `Size.ControlSm` 24 · `Size.Row` 26 ·
`Size.Statusbar` 24 · `Size.Icon` 16. Радиусы — `CornerRadius`, отступы — `Thickness`
там, где нужны как ресурс.

## Шрифтовые стили (классы TextBlock)

heading 16/22/600 · title 14/20/600 · body 13/18/400 · body-medium 13/18/500 · small 12/16/400 ·
caption 11/14/500 · code 12/18 mono · code-small 11/16 mono. `FontFamily`: `Font.Ui`, `Font.Mono`.

## Маппинг старых ключей → новых (для Converters.cs и экранов)

| Старый | Новый |
|---|---|
| Bg.Window | Bg.Base |
| Bg.TitleBar, Bg.Status | Bg.Base |
| Bg.Side, Bg.Panel (острова) | Bg.Surface1 |
| Bg.Panel (карточки внутри) | Bg.Surface2 |
| Line | Border.Subtle (внутри острова) / Border.Default (рамки) |
| Text.Tertiary | Text.Disabled — только для неактивного; для информации → Text.Secondary |
| Accent | Accent.Default (заливка) / Accent.Text (текст, иконки) |
| Ok | Status.Running |
| Warn | Status.Approval |
| Bad | Status.Error |
| Violet (переписка сессий) | Accent.Text + иконка «chat» (второго акцента нет) |
| CloseHover | Bg.Danger |

## Состояния (переходы)

Сессия: Working → `Status.Running` + пульс · WaitingPermission → `Status.Approval` ·
AnsweringPeer → `Status.Waiting` · Crashed → `Status.Error` · Idle/остановлена → `Status.Done`.
Живость СЗ: Online → Running · LagSuspected → Done · NoContact → Approval · откат сломан → Error.
