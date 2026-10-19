# Research: Редизайн Desk на Shirabe

## 1. База контролов: FluentTheme + свои стили или своя тема с нуля

- **Decision**: оставить `FluentTheme` (шаблоны контролов, клавиатура, доступность) и
  перекрыть его ресурсы/стили токенами Shirabe; акцент палитры — `#2f6ae0`.
- **Rationale**: функционал не трогаем; своя тема с нуля — переписывание шаблонов TextBox,
  ListBox, TabControl, ScrollViewer с риском потерять поведение (FR-010).
- **Alternatives**: SimpleTheme + свои шаблоны (больше контроля, кратно больше работы);
  Semi.Avalonia/другая тема (чужой визуальный язык поверх нужного).

## 2. Перекрытие Fluent-ресурсов

- **Decision**: в `Tokens.axaml` переопределить ключи Fluent, которые красят контролы
  (`SystemControlBackground*`, `TextControlBackground*`, `ButtonBackground*`, `ListBoxItem*`,
  `TabItemHeader*`, `ToolTipBackground` и т.п.), значениями токенов; остальное — стилями по
  классам в `Controls.axaml`.
- **Rationale**: часть состояний (pointerover/pressed/focus) Fluent берёт из своих ресурсов —
  без перекрытия hover останется серо-фиолетовым Fluent.

## 3. Шрифты офлайн

- **Decision**: TTF Inter и JetBrains Mono — `AvaloniaResource` в `Assets/Fonts`,
  `FontFamily="avares://SzDiag.Desk/Assets/Fonts#Inter"`; лицензии OFL рядом.
  Файлы берутся из официальных релизов (rsms/inter, JetBrains/JetBrainsMono), не с Google Fonts CDN.
- **Rationale**: бокс может быть без интернета; single-file exe несёт ресурсы внутри.
- **Alternatives**: системные Segoe UI / Cascadia (не тот язык, SC-002), пакет
  Avalonia.Fonts.Inter (только Inter, без Mono — подходит частично; можно использовать для Inter).
- **Табличные цифры**: Avalonia 11 поддерживает `FontFeatures` (`tnum`) у TextBlock —
  выставляется в стилях `body`/`small`/`code*`.

## 4. Иконки

- **Decision**: `StreamGeometry` в `Theme/Icons.axaml`, рисуются `Path` со `Stroke` =
  `Foreground`, `StrokeThickness=1.2`, `StrokeLineCap=Round`. Пути берутся из
  `components/bundle.js` Shirabe (inline SVG) — только нужные Desk.
- **Rationale**: монохром, `currentColor` → `Foreground`, без PNG и шрифтов-иконок.

## 5. Пульс точки работающей сессии

- **Decision**: `Style.Animations` с `Opacity` 1→0,4→1, 1,6 с, только на классе
  `StatusDot.running`.
- **Rationale**: README Shirabe — единственная анимация, кроме спиннера.

## 6. Расход контекста в шапке чата

- **Decision**: не показывать. Desk знает только токены за день (статус-бар), а не
  контекст сессии — новых источников данных спека не вводит.

## 7. Регистр заголовков

- **Decision**: класс `caps` убирается; «ЗАЯВКИ · {0}» → «Заявки · {0}», «БЕЗ АГЕНТА · АРХИВ»
  → «Без агента · архив». Тесты, сверяющие строки, обновляются вместе с разметкой.
