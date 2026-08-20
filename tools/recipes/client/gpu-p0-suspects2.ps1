$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Бинарный поиск держателя P0 среди служб/фоновых процессов MSI (СЗ 161190, после обновления).
#
# Грабля: 17.08 виновником был LEDKeeper2 и гашение процессов СЕССИИ находило его сразу.
# После обновления MSI Center до 2.0.73.0 LEDKeeper2 и DCv2 убиты, а карта осталась в P0 —
# значит держит служба (их gpu-p0-suspects.ps1 не трогал, он работал по процессам сессии).
# Гасим по одному, замер после каждого, стоп на первом, кто отпустил карту в P8.
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-p0-suspects2.ps1

function Get-PState {
    $r = & 'C:\Windows\System32\nvidia-smi.exe' --query-gpu=pstate,clocks.gr,fan.speed,temperature.gpu --format=csv,noheader
    ($r -join ' ')
}
# ВАЖНО (грабля PS): любая строка, выпавшая в output внутри функции, становится её
# возвращаемым значением — из-за этого `if (Wait-Idle 20)` срабатывал на непустом выводе,
# а не на смене pstate, и первый же кандидат объявлялся виновником. Флаг — только через
# $script:released, замеры печатаем в скрипте, а не в функции.
$script:released = $false
function Wait-Idle($sec) {
    $script:released = $false
    $end = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $end) {
        Start-Sleep -Seconds 5
        $s = Get-PState
        Write-Output ("      {0:HH:mm:ss} {1}" -f (Get-Date), $s)
        if ($s -match '^P[2-9]') { $script:released = $true; return }
    }
}

"== старт: " + (Get-PState)

# от безобидного к тяжёлому; службы MSI останавливаем, процессы гасим
$suspects = @(
    @{ Kind = 'proc'; Name = 'MSI.TerminalServer' },
    @{ Kind = 'proc'; Name = 'MSI.CentralServer' },
    @{ Kind = 'svc';  Name = 'LightKeeperService' },
    @{ Kind = 'svc';  Name = 'Mystic_Light_Service' },
    @{ Kind = 'svc';  Name = 'MSI_Case_Service' },
    @{ Kind = 'svc';  Name = 'MSI_Center_Service' },
    @{ Kind = 'proc'; Name = 'uTorrentClients' },
    @{ Kind = 'proc'; Name = 'PhoneExperienceHost' },
    @{ Kind = 'proc'; Name = 'CrossDeviceResume' },
    @{ Kind = 'proc'; Name = 'msedgewebview2' }
)

foreach ($s in $suspects) {
    if ($s.Kind -eq 'svc') {
        $svc = Get-Service $s.Name -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -ne 'Running') { "   пропуск службы {0} (нет/не запущена)" -f $s.Name; continue }
        "== стоп службы {0}" -f $s.Name
        Stop-Service $s.Name -Force -ErrorAction SilentlyContinue
    } else {
        $p = Get-Process $s.Name -ErrorAction SilentlyContinue
        if (-not $p) { "   пропуск процесса {0} (не запущен)" -f $s.Name; continue }
        "== гашу процесс {0} (pid {1})" -f $s.Name, (($p | Select-Object -Expand Id) -join ',')
        $p | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Wait-Idle 20
    if ($script:released) {
        "   >>> ОТПУСТИЛ: {0} — карта ушла из P0" -f $s.Name
        "   финал: " + (Get-PState)
        exit 0
    }
}

'== никто из списка не отпустил карту'
"   финал: " + (Get-PState)
'== что осталось живым из MSI'
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'MSI|LED|Mystic|Light' } |
    ForEach-Object { "   {0,-24} pid {1}" -f $_.Name, $_.Id }
