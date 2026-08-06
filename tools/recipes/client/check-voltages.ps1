$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Что из линий питания на этой машине реально мерится. Гонять ДО того, как строить версию
# про БЖ: наличие +12V — свойство конкретного чипа мониторинга платы, а не общее правило
# (NCT6798D на 160636 отдаёт только 3.3V, NCT6687D на 160705 — полный набор).
# Колонка «уник» — защита от заглушки: одно-единственное значение на весь ряд это не замер,
# а константа, и выдавать её за правду нельзя (бэклог п.71).
# Допуск ATX: +12V = 11,4…12,6 В (±5 %), +5V = 4,75…5,25 В.
#   szcli exec <СЗ> -f tools\recipes\client\check-voltages.ps1
$Csv = 'C:\OCCT\sensors.csv'

$rows = Import-Csv $Csv
$cols = $rows[0].PSObject.Properties.Name
foreach ($c in $cols) {
    if ($c -match 'Voltage\|') {
        $v = foreach ($r in $rows) { if ($r.$c) { [double]::Parse($r.$c, [cultureinfo]::InvariantCulture) } }
        if (-not $v) { continue }
        $m = $v | Measure-Object -Minimum -Maximum -Average
        '{0,-46} мин={1,7:N3} сред={2,7:N3} макс={3,7:N3} уник={4}' -f `
            ($c -replace '\|/[^|]*$', ''), $m.Minimum, $m.Average, $m.Maximum, ($v | Sort-Object -Unique).Count
    }
}
"замеров: $($rows.Count)"
