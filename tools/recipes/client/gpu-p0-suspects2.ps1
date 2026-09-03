$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Бинарный поиск держателя P0 среди служб/фоновых процессов MSI (СЗ 161190, после обновления).
#
# Грабля: 17.08 виновником был LEDKeeper2 и гашение процессов СЕССИИ находило его сразу.
# После обновления MSI Center до 2.0.73.0 LEDKeeper2 и DCv2 убиты, а карта осталась в P0 —
# значит держит служба (их gpu-p0-suspects.ps1 не трогал, он работал по процессам сессии).
# Гасим по одному, замер после каждого, стоп на первом, кто отпустил карту в P8.
#
# #141 / б.194 (161190, 20.08): окно ожидания 20 с оказалось смертельно коротким — карта
# отпускает P0 с задержкой в НЕСКОЛЬКО МИНУТ после смерти настоящего держателя. Перебор
# дошёл до `uTorrentClients.exe`, и ровно на нём карта ушла в P8 — но это было отложенное
# отпускание от `LEDKeeper2`, убитого пятью шагами раньше, а не заслуга торрента. Обратное
# включение (вернули MSI-софт, торрент оставили мёртвым) доказало: карта тут же снова P0.
# Отсюда три правила, это НЕ вкус, а нижняя граница:
#   1. Окно ожидания после гашения — НЕ МЕНЬШЕ 3 МИНУТ ($MinWaitSec ниже).
#   2. Найденный «виновник» — гипотеза, а не вердикт, пока не подтверждён ОБРАТНЫМ
#      включением (вернули — P0 вернулся). Односторонний результат светит подозрением,
#      а не печатает финальный ответ.
#   3. Кандидатов гасим группами «сначала подозреваемые (по железу/подсветке), потом фон
#      (случайные процессы сессии)» — между группами пауза с отдельным замером, чтобы
#      отложенное отпускание не приписалось первому кандидату следующей группы.
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-p0-suspects2.ps1

$MinWaitSec = 180   # 3 минуты — нижняя граница, проверено 161190. Меньше нельзя.

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
        Start-Sleep -Seconds 15
        $s = Get-PState
        Write-Output ("      {0:HH:mm:ss} {1}" -f (Get-Date), $s)
        if ($s -match '^P[2-9]') { $script:released = $true; return }
    }
}

"== старт: " + (Get-PState)

# Группа 1 — «подозреваемые»: софт подсветки/железа MSI, прямой кандидат по цепочке
# LEDKeeper2 -> Mystic_Light_Service. Группа 2 — «фон»: случайные процессы сессии, попавшие
# в перебор не по гипотезе, а потому что были живы (161190 — так и обвинили uTorrent).
$suspects = @(
    @{ Kind = 'proc'; Name = 'MSI.TerminalServer' },
    @{ Kind = 'proc'; Name = 'MSI.CentralServer' },
    @{ Kind = 'svc';  Name = 'LightKeeperService' },
    @{ Kind = 'svc';  Name = 'Mystic_Light_Service' },
    @{ Kind = 'svc';  Name = 'MSI_Case_Service' },
    @{ Kind = 'svc';  Name = 'MSI_Center_Service' }
)
$background = @(
    @{ Kind = 'proc'; Name = 'uTorrentClients' },
    @{ Kind = 'proc'; Name = 'PhoneExperienceHost' },
    @{ Kind = 'proc'; Name = 'CrossDeviceResume' },
    @{ Kind = 'proc'; Name = 'msedgewebview2' }
)

function Kill-Candidate($s) {
    if ($s.Kind -eq 'svc') {
        $svc = Get-Service $s.Name -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -ne 'Running') { "   пропуск службы {0} (нет/не запущена)" -f $s.Name; return $false }
        "== стоп службы {0}" -f $s.Name
        Stop-Service $s.Name -Force -ErrorAction SilentlyContinue
    } else {
        $p = Get-Process $s.Name -ErrorAction SilentlyContinue
        if (-not $p) { "   пропуск процесса {0} (не запущен)" -f $s.Name; return $false }
        "== гашу процесс {0} (pid {1})" -f $s.Name, (($p | Select-Object -Expand Id) -join ',')
        $p | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    return $true
}

$hypothesis = $null
foreach ($group in @(@{ Title = 'подозреваемые'; Items = $suspects }, @{ Title = 'фон'; Items = $background })) {
    "== группа: $($group.Title)"
    foreach ($s in $group.Items) {
        if (-not (Kill-Candidate $s)) { continue }
        Wait-Idle $MinWaitSec
        if ($script:released) {
            $hypothesis = $s
            "   >>> ГИПОТЕЗА: {0} — карта ушла из P0 после его гашения" -f $s.Name
            "   ТРЕБУЕТСЯ ПОДТВЕРЖДЕНИЕ ОБРАТНЫМ ВКЛЮЧЕНИЕМ: верни {0} и проверь, вернётся ли P0" -f $s.Name
            "   (см. gpu-p0-confirm.ps1 / msi-restore-utorrent-off.ps1 — без этого шага это подозрение, не вердикт)"
            "   финал: " + (Get-PState)
            exit 0
        }
    }
    "== пауза между группами (замер, чтобы отложенное отпускание не приписалось следующей группе)"
    Start-Sleep -Seconds 30
    "   $(Get-PState)"
}

'== никто из списка не отпустил карту'
"   финал: " + (Get-PState)
'== что осталось живым из MSI'
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'MSI|LED|Mystic|Light' } |
    ForEach-Object { "   {0,-24} pid {1}" -f $_.Name, $_.Id }
