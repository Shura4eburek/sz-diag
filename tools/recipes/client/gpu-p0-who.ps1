$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# «Кто ещё держит карту в P0», когда очевидные подозреваемые уже мертвы (СЗ 161190, 25.08:
# LEDKeeper2 и DCv2 убиты, карта всё равно P0 1792/7201 при util 0 %).
#
# Модули чужих процессов через Get-Process .Modules под SYSTEM не читаются (тихо даёт пусто),
# поэтому идём через счётчики производительности: GPU Engine показывает per-pid активность,
# GPU Process Memory — кто вообще держит контекст на карте. Долгая серия нужна потому, что
# карта отпускает P0 с задержкой в минуты (бэклог п.194).
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-p0-who.ps1 --timeout 700

$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
function Pstate { (& $smi --query-gpu=pstate,utilization.gpu,clocks.current.graphics,clocks.current.memory,fan.speed,temperature.gpu --format=csv,noheader,nounits) -join '' }

function Holders {
    # имя счётчика локализовано, поэтому берём по englishName через Get-Counter -ListSet
    $rows = @()
    try {
        $c = Get-Counter '\GPU Process Memory(*)\Local Usage' -ErrorAction Stop
        foreach ($s in $c.CounterSamples) {
            if ($s.CookedValue -le 0) { continue }
            if ($s.InstanceName -notmatch 'pid_(\d+)') { continue }
            $procId = [int]$Matches[1]
            $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
            if (-not $p) { continue }
            $rows += [pscustomobject]@{ Pid = $procId; Name = $p.ProcessName; Mb = [math]::Round($s.CookedValue / 1MB, 1) }
        }
    } catch { return @() }
    $rows | Group-Object Pid | ForEach-Object {
        $f = $_.Group[0]
        [pscustomobject]@{ Pid = $f.Pid; Name = $f.Name; Mb = ($_.Group | Measure-Object Mb -Sum).Sum }
    } | Sort-Object Mb -Descending
}

'== кто держит память на карте (сейчас)'
Holders | Select-Object -First 15 | ForEach-Object { '   {0,-24} pid {1,-7} {2} МБ' -f $_.Name, $_.Pid, $_.Mb }

'== серия: pstate каждые 30 с (10 минут), рядом — активные процессы 3D-движка'
for ($i = 0; $i -lt 20; $i++) {
    $eng = @()
    try {
        $e = Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -ErrorAction Stop
        $eng = $e.CounterSamples | Where-Object { $_.CookedValue -gt 0.5 -and $_.InstanceName -match 'pid_(\d+)' } |
            ForEach-Object {
                $null = $_.InstanceName -match 'pid_(\d+)'
                $pp = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
                if ($pp) { ('{0}:{1:N0}%' -f $pp.ProcessName, $_.CookedValue) }
            }
    } catch { }
    '   {0}  {1}   3D: {2}' -f (Get-Date -Format 'HH:mm:ss'), (Pstate), (($eng | Select-Object -Unique) -join ' ')
    Start-Sleep -Seconds 30
}
'== если карта так и не ушла из P8 — при мёртвых LEDKeeper2/DCv2 держатель третий или залипание после TDR'
