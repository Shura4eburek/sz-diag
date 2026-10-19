# Implementation Plan: Редизайн Desk на дизайн-систему Shirabe

**Branch**: `2027-desk-shirabe-design` | **Date**: 2026-10-19 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/001-desk-shirabe-design/spec.md`

## Summary

Desk переводится на визуальный язык Shirabe без изменения поведения. Технически: старый
`Theme/Tokens.axaml` заменяется ресурсами с именами токенов Shirabe (`Bg.Base`, `Bg.Surface1`, …),
`Theme/Controls.axaml` разбивается на стили компонентов дизайн-системы (Button, TextBox,
ListBoxItem, TabItem, ToggleButton, ProgressBar, ToolTip, …), окно собирается из островов
на раме, шрифты Inter / JetBrains Mono встраиваются в сборку, эмодзи заменяются набором
`Path`-иконок. ViewModel'и не трогаются — кроме конвертеров цвета, которые переезжают на новые
ключи статусов.

## Technical Context

**Language/Version**: C# / .NET 8 (`net8.0`)
**Primary Dependencies**: Avalonia 11.3.22 (+ Themes.Fluent как база контролов), Markdown.Avalonia.Tight 11.0.3, CommunityToolkit.Mvvm
**Storage**: N/A
**Testing**: xUnit + Avalonia.Headless (`tests/SzDiag.Desk.Tests`, 198 тестов, smoke-тесты ищут контролы по `x:Name`)
**Target Platform**: Windows 10/11 x64, сервисный бокс (может быть без интернета)
**Project Type**: desktop-app
**Performance Goals**: без регресса — опрос hub и лента чата не тормозят от новых стилей (одна анимация: пульс точки)
**Constraints**: офлайн-шрифты внутри exe; single-file публикация `build-dist`; файлы UTF-8 с BOM не требуются для axaml/cs (требование BOM — для ps1)
**Scale/Scope**: ~16 axaml-файлов, 1 файл конвертеров, ~780 строк разметки

## Constitution Check

`.specify/memory/constitution.md` — незаполненный шаблон, принципов нет. Действуют правила
`CLAUDE.md`: комментарии/вывод на русском, функционал не теряется, тесты зелёные. Гейты пройдены.
Повторная проверка после дизайна — без нарушений.

## Project Structure

### Documentation (this feature)

```text
specs/001-desk-shirabe-design/
├── plan.md
├── research.md
├── data-model.md        # токены Shirabe → ключи ресурсов Avalonia, маппинг старых ключей
├── quickstart.md        # как проверить
└── contracts/
    └── ui-contract.md   # что обязано сохраниться: x:Name, привязки, ключи статусов
```

### Source Code (repository root)

```text
src/SzDiag.Desk/
├── App.axaml                     # FluentTheme + акцент #2f6ae0, подключение Theme/*
├── Assets/Fonts/                 # Inter (Regular/Medium/SemiBold), JetBrains Mono (Regular) + OFL.txt
├── Theme/
│   ├── Tokens.axaml              # ПЕРЕПИСАН: цвета/размеры/радиусы/шрифты Shirabe
│   ├── Typography.axaml          # НОВЫЙ: классы TextBlock.heading/title/body/…/code-small
│   ├── Icons.axaml               # НОВЫЙ: StreamGeometry иконок 16×16 (bolt, snow, stop, chat, terminal, panel-right, …)
│   └── Controls.axaml            # ПЕРЕПИСАН: стили компонентов, 5 состояний
├── Views/
│   ├── MainWindow.axaml          # рама + острова, колонки 268 · * · 308
│   ├── TitleBar.axaml            # тулбар 40, кнопки окна 46×40, иконка restore
│   ├── SzListView.axaml, ChatView.axaml, InspectorView.axaml, Inspector/*.axaml,
│   ├── TransfersView.axaml, StatusBarView.axaml
│   └── Converters.cs             # ключи статусов → Status.*
tests/SzDiag.Desk.Tests/
└── ThemeTests.cs                 # НОВЫЙ: токены на месте, нет hex в Views/*.axaml, шрифты грузятся
```

**Structure Decision**: Один проект Desk, тема — в `Theme/`. Новый проект/библиотеку под
дизайн-систему не заводим: потребитель один.

## Complexity Tracking

Нарушений нет.
