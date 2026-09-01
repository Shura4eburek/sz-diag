$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Перед отправкой накопителя по гарантии (162938 - дохлый NVMe) надо знать, есть ли на нём
# данные клиента: уехавший диск не вернуть (память pull-client-data-before-part-ships).
# Считаем ТОЛЬКО пользовательские папки профилей, вширь и с ограничением по глубине -
# рекурсивный обход умирающего диска виснет на bad-блоках.
$profiles = @(Get-ChildItem 'W:\Users' -Directory -EA SilentlyContinue |
    Where-Object { $_.Name -notin @('Public', 'Default', 'Default User', 'All Users') })
('профилей: ' + $profiles.Count)
foreach ($p in $profiles) {
    ('=== ' + $p.Name + '  создан ' + $p.CreationTime.ToString('yyyy-MM-dd') + '  изменён ' + $p.LastWriteTime.ToString('yyyy-MM-dd'))
    foreach ($sub in @('Desktop', 'Documents', 'Downloads', 'Pictures', 'Videos', 'Music', 'OneDrive')) {
        $d = Join-Path $p.FullName $sub
        if (-not (Test-Path $d)) { continue }
        try {
            $f = @(Get-ChildItem $d -Recurse -File -EA SilentlyContinue)
            $mb = if ($f.Count) { [math]::Round((($f | Measure-Object Length -Sum).Sum / 1MB), 1) } else { 0 }
            ('    {0,-10} {1,5} файлов  {2,8} MB' -f $sub, $f.Count, $mb)
        }
        catch { ('    ' + $sub + ' - ошибка чтения: ' + $_.Exception.Message) }
    }
}
''
'=== установленный софт (верхний уровень Program Files) ==='
foreach ($pf in @('W:\Program Files', 'W:\Program Files (x86)')) {
    foreach ($d in (Get-ChildItem $pf -Directory -EA SilentlyContinue)) { ('  ' + $d.Name) }
}
''
'=== крупные папки в корне диска (кроме системных) ==='
foreach ($d in (Get-ChildItem 'W:\' -Directory -EA SilentlyContinue | Where-Object { $_.Name -notin @('Windows', 'Program Files', 'Program Files (x86)', 'ProgramData', 'Users', '$Recycle.Bin', 'System Volume Information', 'Recovery') })) {
    ('  ' + $d.Name + '  (изменена ' + $d.LastWriteTime.ToString('yyyy-MM-dd') + ')')
}
