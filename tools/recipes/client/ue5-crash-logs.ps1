# Рецепт: разбор крэш-логов Unreal Engine 5 на клиенте (СЗ 123123 — «вылетают игры на UE5»).
# Грабля: diag показывает только TDR-счётчики, а причина (DEVICE_HUNG / DEVICE_REMOVED / out of VRAM /
# Assertion) живёт в <Game>\Saved\Crashes\*\<Game>.log. Плюс хронология TDR по месяцам и история драйверов GPU.
$ErrorActionPreference = 'SilentlyContinue'
"=== TDR 0x141/0x193 по месяцам (WER LiveKernelEvent) ==="
Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1001; ProviderName='Windows Error Reporting'} |
  ? { $_.Message -match 'LiveKernelEvent' } | % { $_.TimeCreated.ToString('yyyy-MM') } | group | sort Name | % { "$($_.Name): $($_.Count)" }
"=== Установки/обновления драйвера видео (Setup/Application, 2026) ==="
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='MsiInstaller'} |
  ? { $_.Message -match 'AMD|Radeon|Adrenalin' } | sort TimeCreated | % { "$($_.TimeCreated.ToString('MM-dd HH:mm')) $($_.Message.Substring(0,[Math]::Min(110,$_.Message.Length)))" } |
  Select-Object -Last 40
"=== Драйвер дисплея: версии в DriverStore ==="
Get-ChildItem C:\Windows\System32\DriverStore\FileRepository -Directory -Filter 'u0*' | select Name,LastWriteTime | ft -AutoSize | Out-String
"=== UE crash folders ==="
$dirs = Get-ChildItem 'C:\Users\*\AppData\Local\*\Saved\Crashes' -Directory
foreach ($g in $dirs) {
  $cr = Get-ChildItem $g.FullName -Directory | sort LastWriteTime
  "--- $($g.FullName): $($cr.Count) крэшей, первый $($cr[0].LastWriteTime), последний $($cr[-1].LastWriteTime)"
  $cr | % { $_.LastWriteTime.ToString('yyyy-MM') } | group | sort Name | % { "   $($_.Name): $($_.Count)" }
  foreach ($c in ($cr | select -Last 3)) {
    $log = Get-ChildItem $c.FullName -Filter *.log | select -First 1
    if (-not $log) { continue }
    "   == $($c.Name)"
    $txt = Get-Content $log.FullName
    $txt | Select-String 'LogInit: Engine Version|LogRHI:.*(Adapter|Driver)|LogD3D12RHI: .*(Driver|Version)' | select -First 4 | % { "   " + $_.Line.Trim() }
    $txt | Select-String 'Fatal error|GPU crash|DXGI_ERROR|Unhandled Exception|EXCEPTION_|VK_ERROR|Assertion failed|out of video memory|Out of memory|Shader compilation|PSO' |
      select -First 8 | % { "   " + $_.Line.Substring(0,[Math]::Min(200,$_.Line.Length)).Trim() }
    # хвост лога — что было прямо перед смертью
    "   ... хвост:"; $txt | select -Last 6 | % { "   " + $_.Substring(0,[Math]::Min(200,$_.Length)) }
  }
}
