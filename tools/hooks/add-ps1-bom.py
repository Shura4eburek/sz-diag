"""PostToolUse-хук: дописывает UTF-8 BOM любому .ps1 сразу после Write/Edit.

Зачем. PowerShell 5.1 читает файл без BOM в системной ANSI-кодировке: кириллица в
строках разъезжается, и скрипт падает с `The string is missing the terminator` или
`Unexpected token '}'`. Ошибка выглядит синтаксической и уводит чинить заведомо
исправный код. Требование «UTF-8 с BOM» записано в CLAUDE.md и tools/recipes/README.md,
но соблюдается только если помнить о нём в момент создания файла — на практике не
помнилось, отсюда хук.

Читает JSON хука со stdin, берёт путь файла, и если первых трёх байт EF BB BF нет —
дописывает их в начало. Содержимое не трогает.
"""
import json
import sys

BOM = b"\xef\xbb\xbf"


def main() -> None:
    try:
        data = json.load(sys.stdin)
    except Exception:
        return  # не наш формат — молча выходим, хук не должен ломать работу

    tool_response = data.get("tool_response") or {}
    tool_input = data.get("tool_input") or {}
    path = ""
    if isinstance(tool_response, dict):
        path = tool_response.get("filePath") or ""
    if not path:
        path = tool_input.get("file_path") or ""

    if not path.lower().endswith(".ps1"):
        return

    try:
        with open(path, "rb") as fh:
            body = fh.read()
    except OSError:
        return  # файл мог быть удалён/переименован — не наше дело

    if body.startswith(BOM):
        return

    try:
        with open(path, "wb") as fh:
            fh.write(BOM + body)
    except OSError:
        return

    # Сообщение в UI: пусть видно, что файл поправлен, а не «само как-то заработало».
    # stdout переводим в UTF-8 явно: под cp866/cp1251 кириллица в выводе бьётся.
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    print(json.dumps({"systemMessage": "BOM дописан: " + path}, ensure_ascii=False))


if __name__ == "__main__":
    main()
