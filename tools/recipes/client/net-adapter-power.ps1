$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# ЭНЕРГОСБЕРЕЖЕНИЕ И РЕЖИМЫ СЕТЕВОГО АДАПТЕРА — только чтение.
#
# Грабля (СЗ 162367): «сетевуха отваливается» на клиентских машинах чаще всего не дефект
# железа, а Green Ethernet / EEE / «разрешить отключение для экономии энергии»: карта
# усыпляет линк, и на PXE-загрузке машина не успевает получить образ — клиент видит
# «не стартує». В `diag.md` этих параметров нет вообще (бэклог п.203), а руками их лезут
# смотреть в «Дополнительно» свойств адаптера — по одному чекбоксу.
#
# Отдельно печатается Speed & Duplex: если он прибит в 100 Мбит вручную, карта 2.5G
# никогда не даст больше, и «медленная сеть» уедет в замену платы вместо снятия галки.
#
#   szcli exec <СЗ> -f tools\recipes\client\net-adapter-power.ps1
# Правки НЕ вносит: любое изменение на клиенте требует парного отката при закрытии СЗ.

$nics = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)
if (-not $nics.Count) { 'физических сетевых адаптеров не видно'; return }

# Ключи, которые реально влияют на «отваливается»/«медленно». Имена свойств у Intel и
# Realtek разные, поэтому ищем по смыслу, а не по точному совпадению.
$watch = 'Energy.Efficient|EEE|Green|Power Saving|Powersaving|Wake on|WakeOn|Speed.*Duplex|' +
         'Flow Control|Interrupt Moderation|Selective Suspend|Gigabit Lite|ASPM|Idle'

foreach ($n in $nics) {
    ''
    "=== $($n.Name) / $($n.InterfaceDescription) ==="
    "    статус $($n.Status), линк $($n.LinkSpeed), драйвер $($n.DriverVersion) от $($n.DriverDate)"

    $pm = Get-NetAdapterPowerManagement -Name $n.Name -ErrorAction SilentlyContinue
    if ($pm) {
        # Это та самая галка «Разрешить отключение этого устройства для экономии энергии».
        # Enabled = винда вправе усыпить карту, и линк моргнёт без всякого дефекта.
        "    отключение для экономии энергии : $($pm.DeviceSleepOnDisconnect)"
        "    Wake-on-Magic / Wake-on-Pattern : $($pm.WakeOnMagicPacket) / $($pm.WakeOnPattern)"
    }

    # Дубль по реестру: PnPCapabilities с битом 0x18 = «отключать нельзя» (галка снята).
    # Нужен потому, что Get-NetAdapterPowerManagement на части драйверов молчит.
    try {
        $inst = Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}' -ErrorAction SilentlyContinue |
                Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).NetCfgInstanceId -eq $n.InterfaceGuid }
        if ($inst) {
            $cap = (Get-ItemProperty $inst.PSPath -Name PnPCapabilities -ErrorAction SilentlyContinue).PnPCapabilities
            if ($null -ne $cap) {
                "    PnPCapabilities = $cap " + $(if ($cap -band 0x18) { '(усыплять запрещено — хорошо)' } else { '(винда может усыплять карту)' })
            } else { '    PnPCapabilities не задан (по умолчанию винда может усыплять карту)' }
        }
    } catch { }

    $adv = @(Get-NetAdapterAdvancedProperty -Name $n.Name -ErrorAction SilentlyContinue |
             Where-Object { $_.DisplayName -match $watch })
    if ($adv.Count) {
        '    --- параметры, влияющие на стабильность линка ---'
        foreach ($a in $adv) { "      {0,-42} {1}" -f $a.DisplayName, $a.DisplayValue }
    } else { '    расширенных параметров не отдаёт (урезанный драйвер)' }
}

''
'--- что из этого лечит «отваливается» ---'
'  Energy Efficient Ethernet / EEE, Green Ethernet, Power Saving Mode  -> Disabled'
'  "Разрешить отключение этого устройства для экономии энергии"        -> снять'
'  Speed & Duplex, если прибит вручную                                 -> Auto Negotiation'
'Менять только с записью в журнал СЗ (szcli note) и с откатом при закрытии.'
