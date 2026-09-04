namespace SzDiag.Contracts;

/// <summary>Цикл «сон → RTC-пробуждение» как воспроизводящий тест дефекта (промотано из
/// рецептов <c>sleep-cycle-test.ps1</c>/<c>sleep-cycle-stop.ps1</c> в CLI, бэклог п.225,
/// СЗ 161498).
///
/// Забытый цикл 24.08 не остановился (стоп-файла не было) и воскрес 26.08 при включении
/// машины, снова уложив её спать пятью циклами подряд — снимать пришлось офлайн из WinPE.
/// Payload инсталлятора раньше был статичным heredoc'ом с захардкоженным <c>$Sz='161498'</c>
/// (heredoc <c>@'...'@</c> — однокавычечный, PowerShell-переменные в нём не раскрываются),
/// поэтому параметризация была невозможна без правки файла на каждую заявку. Генерируя скрипт
/// в C#, подставляем реальный номер СЗ напрямую и штампуем версию в payload — старая правка в
/// репозитории больше не может создать ложное ощущение «на клиенте исправленная версия»
/// (payload несёт свою версию и печатает её в лог на каждом WAKE).</summary>
public static class SleepCycleScript
{
    /// <summary>Версия payload — печатается в лог на каждом пробуждении. Поднимай при
    /// изменении логики цикла, чтобы было видно, какая версия реально работает на клиенте.</summary>
    public const string Version = "2026-09-04.1";

    /// <summary>Инсталлятор: пишет и сразу стартует самоперевзводящийся цикл сна под задачей
    /// SYSTEM. Безопасник по паролю учётки (бэклог б.224) — тест не стартует, если после сна
    /// машина попросит пароль, которого нет, если явно не подтверждён риск.</summary>
    public static string BuildInstall(string sz, int sleepMinutes = 4, int awakeSeconds = 90,
        int maxHours = 8, bool confirmRisk = false)
    {
        var confirmRiskLiteral = confirmRisk ? "$true" : "$false";
        var payload = BuildPayload(sz, sleepMinutes, awakeSeconds);
        var payloadEscaped = payload.Replace("'", "''");
        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            if (-not (Test-Path 'C:\OCCT')) { New-Item -ItemType Directory 'C:\OCCT' | Out-Null }
            Remove-Item 'C:\OCCT\stop-sleep-test' -Force -ErrorAction SilentlyContinue

            $ConfirmRisk = {{confirmRiskLiteral}}

            $logonUser = (Get-CimInstance Win32_ComputerSystem).UserName
            $hasPassword = $false
            $acct = $null
            if ($logonUser) {
                $shortName = $logonUser.Split('\')[-1]
                $acct = Get-LocalUser -Name $shortName -ErrorAction SilentlyContinue
                if ($acct) { $hasPassword = $null -ne $acct.PasswordLastSet }
            }
            $autoLogon = $false
            try {
                $wl = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction Stop
                $autoLogon = ($wl.AutoAdminLogon -eq '1')
            } catch { }

            "tekushiy polzovatel sessii: $logonUser"
            "parol u uchetki (PasswordLastSet): $hasPassword"
            "AutoAdminLogon (deystvuet tolko pri polnoy zagruzke, NE posle sna): $autoLogon"

            if ($hasPassword -and -not $ConfirmRisk) {
                'OSTANOVLENO: u uchetki est parol, a avtologon posle SNA (v otlichie ot zagruzki) Windows ne'
                'primenyaet - posle pervogo zhe probuzhdeniya mashina poprosit parol, kotorogo u nas net, i'
                'sessiya okazhetsya zaperta do prihoda mastera.'
                exit 1
            }
            if ($hasPassword) { 'risk podtverzhden yavno - prodolzhaem' }

            $MaxHours = {{maxHours}}
            [IO.File]::WriteAllText('C:\OCCT\sleep-cycle-deadline', (Get-Date).AddHours($MaxHours).ToString('o'))
            "predohranitel: cikl sam umret posle {0:dd.MM HH:mm:ss}" -f (Get-Date).AddHours($MaxHours)

            $payload = '{{payloadEscaped}}'
            [IO.File]::WriteAllText('C:\OCCT\sleep-cycle.ps1', $payload, (New-Object Text.UTF8Encoding $true))
            "payload zapisan (versiya {{Version}}): {0:N1} KB" -f ((Get-Item 'C:\OCCT\sleep-cycle.ps1').Length / 1KB)

            $err = $null
            [void][Management.Automation.PSParser]::Tokenize((Get-Content 'C:\OCCT\sleep-cycle.ps1' -Raw), [ref]$err)
            if ($err) { throw ("payload ne parsitsya: " + ($err | ForEach-Object { $_.Message } | Select-Object -First 3)) }
            'payload: sintaksis ok'

            '--- startuem pervyi cikl ---'
            & 'C:\OCCT\sleep-cycle.ps1'
            """;
    }

    /// <summary>Тело самоперевзводящегося цикла — параметры (СЗ, интервалы) подставлены
    /// напрямую при генерации, никакого heredoc'а с захардкоженным номером заявки.</summary>
    private static string BuildPayload(string sz, int sleepMinutes, int awakeSeconds) => $$"""
        $Sz            = '{{sz}}'
        $SleepMinutes  = {{sleepMinutes}}
        $AwakeSeconds  = {{awakeSeconds}}
        $PayloadVersion = '{{Version}}'

        $log  = 'C:\OCCT\sleep-test.log'
        $stop = 'C:\OCCT\stop-sleep-test'
        $self = 'C:\OCCT\sleep-cycle.ps1'
        $task = "szdiag-sleepcycle-$Sz"

        function Say($m) {
            $line = '{0:yyyy-MM-dd HH:mm:ss}  [v{1}]  {2}' -f (Get-Date), $PayloadVersion, $m
            [IO.File]::AppendAllText($log, $line + "`r`n", [Text.Encoding]::UTF8)
            $line
        }

        $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
        Say ("=== WAKE === zagruzka OS: {0:dd.MM HH:mm:ss}, aptaym {1:hh\:mm\:ss}" -f $boot, ((Get-Date) - $boot))
        $dirty = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 6008 } -MaxEvents 1 -ErrorAction SilentlyContinue
        if ($dirty) {
            Say ("    poslednee GRYAZNOE vyklyuchenie: {0} {1}" -f $dirty.Properties[1].Value, $dirty.Properties[0].Value)
        }
        $w = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = @(42, 107) } -MaxEvents 4 -ErrorAction SilentlyContinue
        if ($w) { Say ('    sobytiya sna: ' + (($w | Sort-Object TimeCreated | ForEach-Object { '{0:HH:mm:ss}/Id{1}' -f $_.TimeCreated, $_.Id }) -join ' ')) }
        Say ('    lastwake: ' + ((powercfg /lastwake) -join ' ').Trim())

        if (Test-Path $stop) { Say 'STOP-fayl na meste - cikl okonchen'; schtasks /delete /tn $task /f 2>$null | Out-Null; return }

        $dl = 'C:\OCCT\sleep-cycle-deadline'
        $expired = $true
        if (Test-Path $dl) {
            try { $expired = ([datetime]::Parse((Get-Content $dl -Raw).Trim()) -lt (Get-Date)) } catch { $expired = $true }
        }
        if ($expired) {
            Say 'PREDOHRANITEL: srok cikla istek (ili fayl dedlayna poteryan) - snimaem zadachu i vyhodim'
            schtasks /delete /tn $task /f 2>$null | Out-Null
            return
        }

        Say "    zhdem $AwakeSeconds s (agent rekonnektitsya k hub)"
        Start-Sleep -Seconds $AwakeSeconds
        if (Test-Path $stop) { Say 'STOP-fayl poyavilsya - vyhodim'; schtasks /delete /tn $task /f 2>$null | Out-Null; return }

        $at = (Get-Date).AddMinutes($SleepMinutes)
        $xml = @"
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>szdiag: cikl sna dlya SZ $Sz</Description></RegistrationInfo>
          <Triggers><TimeTrigger><StartBoundary>$($at.ToString('yyyy-MM-ddTHH:mm:ss'))</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>true</WakeToRun>
            <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
            <Priority>5</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>powershell.exe</Command>
              <Arguments>-NoProfile -ExecutionPolicy Bypass -File "C:\OCCT\sleep-cycle.ps1"</Arguments>
            </Exec>
          </Actions>
        </Task>
        "@
        $tmp = Join-Path $env:TEMP "$task.xml"
        [IO.File]::WriteAllText($tmp, $xml, [Text.Encoding]::Unicode)
        schtasks /delete /tn $task /f 2>$null | Out-Null
        $create = schtasks /create /tn $task /xml $tmp /f 2>&1
        Say ('    zadacha probuzhdeniya na {0:HH:mm:ss}: {1}' -f $at, ($create -join ' '))
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue

        $wt = (powercfg /waketimers) -join ' '
        Say ('    waketimers: ' + $wt.Trim())
        if ($wt -match 'no active wake timers') { Say '!!! taymer NE vzveden - son otmenyaem, inache mashina ne prosnetsya'; return }

        Say '=== SLEEP === uhodim v son'
        rundll32.exe powrprof.dll,SetSuspendState 0,1,0
        """;

    /// <summary>Стоп: ставит стоп-файл, снимает задачу пробуждения (по маске — СЗ уже не
    /// нужен, задача сама себя называет), печатает лог теста целиком.</summary>
    public const string StopScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        New-Item -ItemType File 'C:\OCCT\stop-sleep-test' -Force | Out-Null
        Get-ChildItem 'C:\Windows\System32\Tasks' -Filter 'szdiag-sleepcycle-*' -ErrorAction SilentlyContinue |
            ForEach-Object { schtasks /delete /tn $_.Name /f 2>&1 | Out-Null; "zadacha snyata: $($_.Name)" }
        $log = 'C:\OCCT\sleep-test.log'
        if (Test-Path $log) { '--- log testa ---'; Get-Content $log -Encoding UTF8 } else { 'loga net' }
        """;
}
