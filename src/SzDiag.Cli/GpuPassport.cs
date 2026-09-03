namespace SzDiag.Cli;

/// <summary>Паспорт видеокарты для заявки в АСЦ одной командой (`szcli hw passport &lt;СЗ&gt;`).
///
/// Регрессия (бэклог п.146, СЗ 160705): карту отправили в авторизованный сервис, и в заявку
/// понадобились SUBSYS (отличает партнёрскую плату от референса) и part number vBIOS — ни
/// `diag система`, ни `diag gpu` их не отдавали. Снимали отдельным рецептом
/// (<c>tools/recipes/client/gpu-passport.ps1</c>) уже после того, как машина ушла под прогон.
/// Здесь — тот же текст встроенным в CLI, чтобы паспорт снимался одной командой и без файла
/// рецепта рядом с exe.</summary>
public static class GpuPassport
{
    /// <summary>Скрипт гоняется через `exec` (EncodedCommand, UTF-16) — кириллица не проблема,
    /// в отличие от секций <c>DiagnosticProbes</c> (те уходят агенту иным путём).</summary>
    public const string GpuScript = """
        '=== Видеокарта ==='
        Get-CimInstance Win32_VideoController | ForEach-Object {
            ('Название           : ' + $_.Name)
            ('PNPDeviceID        : ' + $_.PNPDeviceID)
            ('Версия драйвера    : ' + $_.DriverVersion + '  (от ' + ($_.DriverDate) + ')')
            ('Видеопамять, ГБ    : ' + [math]::Round($_.AdapterRAM / 1GB, 1) + '   (значение врёт на картах > 4 ГБ — сверяться с моделью)')
            ('vBIOS              : ' + $_.VideoProcessor + ' / ' + $_.AdapterCompatibility)
            ('Статус             : ' + $_.Status + ', ошибка конфигурации: ' + $_.ConfigManagerErrorCode)
            if ($_.PNPDeviceID -match 'SUBSYS_([0-9A-Fa-f]{8})') {
                ('SUBSYS             : SUBSYS_' + $matches[1])
            }
            ''
        }

        '=== Реестр драйвера: точная плата и BIOS ==='
        function Convert-HwString($v) {
            if ($null -eq $v) { return $null }
            if ($v -is [string]) { return $v }
            ((($v | ForEach-Object { [char][int]$_ }) -join '') -replace "`0", '').Trim()
        }
        Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
                $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                if ($p.DriverDesc) {
                    ('DriverDesc         : ' + $p.DriverDesc)
                    foreach ($k in @('AdapterString','BiosString','ChipType','DacType','MemorySize')) {
                        $val = Convert-HwString $p."HardwareInformation.$k"
                        if ($val) { ('{0,-18} : {1}' -f $k, $val) }
                    }
                    if ($p.MatchingDeviceId) { ('MatchingDeviceId   : ' + $p.MatchingDeviceId) }
                    ''
                }
            }

        '=== PCIe: слот, ширина, скорость ==='
        Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | ForEach-Object {
            $d = $_
            ('Устройство         : ' + $d.FriendlyName + '  [' + $d.Status + ']')
            ('InstanceId         : ' + $d.InstanceId)
            foreach ($k in @('DEVPKEY_PciDevice_CurrentLinkSpeed','DEVPKEY_PciDevice_CurrentLinkWidth','DEVPKEY_PciDevice_MaxLinkSpeed','DEVPKEY_PciDevice_MaxLinkWidth','DEVPKEY_Device_LocationInfo')) {
                $v = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName $k -ErrorAction SilentlyContinue).Data
                if ($null -ne $v) { ('{0,-18} : {1}' -f ($k -replace 'DEVPKEY_(PciDevice_|Device_)',''), $v) }
            }
            ''
        }

        '=== Ошибки видеодрайвера в журнале (TDR и падения) ==='
        $ev = Get-WinEvent -FilterHashtable @{LogName='System'; Id=4101,4098,14,13} -MaxEvents 40 -ErrorAction SilentlyContinue |
              Where-Object { $_.ProviderName -match 'Display|amdkmdap|nvlddmkm|amdwddmg' }
        if ($ev) { $ev | Select-Object -First 10 TimeCreated,Id,ProviderName | Format-Table -Auto | Out-String -Width 120 }
        else { 'событий TDR/падений видеодрайвера нет' }
        """;

    /// <summary>Известные области паспорта. Пока реализована только `gpu` — она и была
    /// критерием готовности п.146; `all` подключит cpu/storage паспорта по мере надобности.</summary>
    public static string? ScriptFor(string scope) => scope.ToLowerInvariant() switch
    {
        "gpu" => GpuScript,
        _ => null
    };
}
