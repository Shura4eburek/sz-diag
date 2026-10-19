# UI-контракт: что редизайн обязан сохранить

Desk не имеет внешнего API; контракт — то, на что опираются тесты и код окна.

## Имена контролов (`x:Name`), которые ищут тесты и code-behind

`TitleBar`, `DragArea`, `MinButton`, `MaxButton`, `CloseButton`, `LeftPane`, `CenterPane`,
`InspectorPane`, `StatusPane`, `InspectorToggle`, `TerminalButton`, `StartSessionPanel`,
`OfflineSessionPanel`, `OfflineSzBox`, `ChatPane`, `ChatInput`, `FeedScroll`, `List`,
`FrozenBadge`, `InspectorTabs` — сохраняются с тем же типом контрола (или его наследником).

## Привязки

Все `{Binding}` к ViewModel'ям сохраняются по путям; ViewModel'и не меняются, кроме
`Converters.cs` (только ключи ресурсов и текст `⚠` → без эмодзи).

## Ресурсы

- Ключи `Bg.*`, `Border.*`, `Text.*`, `Accent.*`, `Status.*`, `Space.*`, `Radius.*`, `Size.*`,
  `Font.Ui`, `Font.Mono` — по [data-model.md](../data-model.md); старые ключи удаляются,
  ссылок на них не остаётся.
- В `Views/**/*.axaml` нет литералов цвета (`#rrggbb`, `#aarrggbb`) — только `StaticResource`.

## Классы стилей (публичные для экранов)

`Button.primary|secondary|danger|ghost|caption|caption.close`, `ToggleButton.icon`,
`Border.island|card|code`, `TextBlock.heading|title|body|body-medium|small|caption|code|code-small|secondary`,
`Ellipse.dot.running|approval|error|waiting|done`, `Path.icon`.
