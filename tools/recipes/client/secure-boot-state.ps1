$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# СОСТОЯНИЕ SECURE BOOT / TPM / КЛЮЧЕЙ UEFI — снимается за одну команду.
#
# Грабля (СЗ 162367, ПК-клуб на PXE, 25.08.2026): машина приехала с фото «Secure Boot Violation»,
# а шоурум просил перешить BIOS на старую версию 3222 «как у клиента». Без приборного снимка
# спор упирается в мнения. Снимок решил вопрос за минуту: Secure Boot ВКЛЮЧЁН, ключи штатные,
# TPM 2.0 готов, и при этом обычная подписанная Windows с тестового SSD грузится нормально —
# значит Violation ловит ИХ PXE-загрузчик (не подписан либо отозван свежим dbx), а не железо.
# Заодно `dbx` размером в десятки КБ = свежий список отзывов: то, что грузилось на старом BIOS,
# на новом отзывается — и откатом это не лечится (ASUS помечает часть версий необратимыми).
#
# Для античитов (Vanguard/EAC/FACEIT) важен сам факт «Secure Boot = Enabled», а не чьи ключи, —
# поэтому решение ищем в подписи загрузчика или своём сертификате в db, а не в выключении.
#
#   szcli exec <СЗ> -f tools\recipes\client\secure-boot-state.ps1

'== BIOS =='
$bios = Get-CimInstance Win32_BIOS
'{0} {1} ({2:dd.MM.yyyy})' -f $bios.Manufacturer, $bios.SMBIOSBIOSVersion, $bios.ReleaseDate
'плата: ' + (Get-CimInstance Win32_BaseBoard).Product
'режим загрузки: ' + $env:firmware_type

'== Secure Boot =='
try { 'включён: ' + (Confirm-SecureBootUEFI) } catch { 'нет данных: ' + $_.Exception.Message }
foreach ($n in 'SetupMode', 'PK', 'KEK', 'db', 'dbx') {
    try {
        $v = Get-SecureBootUEFI -Name $n -ErrorAction Stop
        if ($n -eq 'SetupMode') { 'SetupMode = {0} (1 = ключи не установлены)' -f $v.Bytes[0] }
        else { '{0}: {1} байт' -f $n, $v.Bytes.Length }
    } catch { '{0}: {1}' -f $n, $_.Exception.Message }
}

'== TPM =='
try { Get-Tpm | Select-Object TpmPresent, TpmReady, TpmEnabled, ManufacturerVersion | Format-List | Out-String } catch { $_.Exception.Message }
