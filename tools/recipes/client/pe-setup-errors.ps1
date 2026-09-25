$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Разбор упавшей установки Windows из PE (СЗ 162581: "не ставится винда на SSD").
#
# Грабля: сообщение установщика на экране («не удалось скопировать файлы» и т.п.) ничего
# не говорит о причине — реальный код ошибки лежит в Panther-логах на ЦЕЛЕВОМ диске,
# в каталоге $Windows.~BT, который остаётся после срыва. Ищем его на всех томах,
# берём setuperr.log целиком и хвост setupact.log вокруг первой ошибки.

$found = $false
foreach ($v in (Get-Volume | Where-Object { $_.DriveLetter -and $_.FileSystem -eq 'NTFS' })) {
    $root = ($v.DriveLetter + ':\')
    foreach ($rel in '$Windows.~BT\Sources\Panther', 'Windows\Panther', '$WINDOWS.~BT\Sources\Panther') {
        $dir = Join-Path $root $rel
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $found = $true
        Write-Output ('=== ' + $dir + ' ===')
        Get-ChildItem -LiteralPath $dir -Filter 'setup*.log' -ErrorAction SilentlyContinue |
            Select-Object Name, Length, LastWriteTime | Format-Table -Auto | Out-String -Width 200

        $err = Join-Path $dir 'setuperr.log'
        if (Test-Path -LiteralPath $err) {
            Write-Output '--- setuperr.log (последние 60 строк) ---'
            Get-Content -LiteralPath $err -Tail 60 -ErrorAction SilentlyContinue
        }

        $act = Join-Path $dir 'setupact.log'
        if (Test-Path -LiteralPath $act) {
            Write-Output '--- setupact.log: строки с Error/Warning (последние 60) ---'
            Select-String -LiteralPath $act -Pattern 'Error|0x8|failed|Callback_' -ErrorAction SilentlyContinue |
                Select-Object -Last 60 | ForEach-Object { $_.Line }
            Write-Output '--- setupact.log (хвост 40 строк) ---'
            Get-Content -LiteralPath $act -Tail 40 -ErrorAction SilentlyContinue
        }
    }
}
if (-not $found) { Write-Output 'Panther-логи не найдены ни на одном NTFS-томе' }
