#!/bin/bash
# Установка SzDiag на маке: «SzDiag.app» в /Applications (Launchpad, Spotlight, Dock) и szcli в PATH.
#
#   tools/install-mac.sh              поставить / обновить
#   tools/install-mac.sh --uninstall  снять приложение и szcli (dist и данные не трогаются)
#
# Приложение — тонкая обёртка над dist/host/desk/SzDiag.Desk: бинарь, конфиг и данные Desk
# (сессии, логи, токены) остаются в dist, поэтому пересборка через run.sh / build-dist-mac.sh
# переустановки не требует. Переустановка нужна только при переносе репозитория.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
desk="$root/dist/host/desk/SzDiag.Desk"
cli="$root/dist/host/cli/SzDiag.Cli"
app_name="SzDiag.app"
bin_dir="$HOME/.local/bin"

# /Applications пишется группой admin без sudo; если нет — ~/Applications (тоже видит Spotlight).
if [ -w /Applications ]; then apps_dir=/Applications; else apps_dir="$HOME/Applications"; fi
app="$apps_dir/$app_name"

if [ "${1:-}" = "--uninstall" ]; then
    rm -rf "/Applications/$app_name" "$HOME/Applications/$app_name"
    rm -f "$bin_dir/szcli"
    echo "Снято: $app_name, $bin_dir/szcli"
    exit 0
fi

[ -x "$desk" ] || { echo "Нет $desk — сначала ./run.sh build." >&2; exit 1; }

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT
bundle="$staging/$app_name"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"

# Иконка: тот же знак «пульс», что у exe на винде. iconset собираем из 256px слоя .ico.
iconset="$staging/szdiag.iconset"
mkdir -p "$iconset"
sips -s format png "$root/src/SzDiag.Desk/Assets/szdiag.ico" --out "$staging/icon.png" >/dev/null
for size in 16 32 128 256; do
    sips -z $size $size "$staging/icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z $double $double "$staging/icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$bundle/Contents/Resources/szdiag.icns"

version="$(git -C "$root" rev-parse --short HEAD 2>/dev/null || echo dev)"
cat > "$bundle/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>SzDiag</string>
  <key>CFBundleDisplayName</key><string>SzDiag</string>
  <key>CFBundleIdentifier</key><string>local.szdiag.desk</string>
  <key>CFBundleExecutable</key><string>SzDiag</string>
  <key>CFBundleIconFile</key><string>szdiag</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF

# Запуск из Finder/Dock идёт с голым PATH (/usr/bin:/bin...) — без brew и ~/.local/bin Desk не
# нашёл бы git, cloudflared и claude, если они в конфиге не абсолютные.
cat > "$bundle/Contents/MacOS/SzDiag" <<EOF
#!/bin/bash
export PATH="\$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:\$PATH"
desk="$desk"
if [ ! -x "\$desk" ]; then
    /usr/bin/osascript -e 'display alert "SzDiag" message "Не найден '"\$desk"'. Собери dist: ./run.sh build"' >/dev/null
    exit 1
fi
cd "\$(dirname "\$desk")" || exit 1
exec "\$desk"
EOF
chmod +x "$bundle/Contents/MacOS/SzDiag"

rm -rf "$app"
mkdir -p "$apps_dir"
mv "$bundle" "$app"
# Обновить кэш LaunchServices, иначе Finder может показывать старую иконку.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
    -f "$app" >/dev/null 2>&1 || true

# szcli: обёртка с абсолютным путём (симлинк на dist/host/szcli сломал бы его `dirname $0`).
mkdir -p "$bin_dir"
cat > "$bin_dir/szcli" <<EOF
#!/bin/bash
exec "$cli" "\$@"
EOF
chmod +x "$bin_dir/szcli"

echo "== Установлено =="
echo "Приложение: $app (Launchpad / Spotlight «SzDiag»; в Dock — правый клик → Оставить в Dock)"
echo "szcli:      $bin_dir/szcli"
case ":$PATH:" in
    *":$bin_dir:"*) ;;
    *) echo "Внимание: $bin_dir не в PATH — добавь в ~/.zshrc: export PATH=\"\$HOME/.local/bin:\$PATH\"" ;;
esac
