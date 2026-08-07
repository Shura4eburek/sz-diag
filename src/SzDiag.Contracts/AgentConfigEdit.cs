namespace SzDiag.Contracts;

/// <summary>Правка конфига агента на клиенте с хоста — без ручного лазания в файл.
///
/// Боль (бэклог п.86, СЗ 160306): у клиента в `appsettings.json` стоял `WatchdogHours: 1`,
/// и поднять его на работающей машине было нельзя — агент читает конфиг один раз при старте,
/// а перезапустить его удалённо тогда было нечем (п.83). Тупик: значение неверное, поменять
/// можно, применить — нет.
///
/// Здесь правка + **немедленное применение** там, где это возможно: `WatchdogHours`
/// перевзводит watchdog-задачу с новым сроком (`-Force`, как в `Resume`). Остальные ключи
/// вступают в силу при следующем открытии доступа — об этом скрипт говорит прямо.</summary>
public static class AgentConfigEdit
{
    /// <summary>Ключи, которые применяются сразу (без переоткрытия доступа).</summary>
    public static readonly string[] HotKeys = { "WatchdogHours", "HeartbeatSeconds" };

    /// <summary>Ключи, которые агент прочитает только при следующем `Open`.</summary>
    public static readonly string[] RestartKeys =
        { "ServiceAccount", "SshPort", "HubUrl", "SshBinDir", "SshWorkDir", "StatePath" };

    /// <summary>Разбор `Ключ=значение`. null — форма не та.</summary>
    public static (string Key, string Value)? ParseAssignment(string text)
    {
        var idx = (text ?? "").IndexOf('=');
        if (idx <= 0) return null;
        var key = text![..idx].Trim();
        var value = text[(idx + 1)..].Trim();
        return key.Length == 0 || value.Length == 0 ? null : (key, value);
    }

    /// <summary>Известен ли ключ вообще — опечатка в имени не должна молча уезжать в конфиг.</summary>
    public static bool IsKnownKey(string key)
        => HotKeys.Concat(RestartKeys).Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Скрипт правки: находит `appsettings.json` рядом с живым agent.exe, меняет ключ,
    /// сохраняет и (для `WatchdogHours`) перевзводит watchdog с новым сроком.</summary>
    /// <param name="sz">Номер СЗ — по нему собирается имя watchdog-задачи.</param>
    public static string BuildScript(string sz, string key, string value)
    {
        var numeric = double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _);
        // Числа кладём числом, строки — строкой: JSON с "6" вместо 6 биндер прочитает, но
        // конфиг станет нечитаемым для человека.
        var jsonValue = numeric ? value : $"'{value.Replace("'", "''")}'";

        return $$"""
            $ErrorActionPreference = 'Stop'
            $proc = Get-Process -Name 'SzDiag.Agent' -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $proc -or -not $proc.Path) { 'ОШИБКА: агент не найден — конфиг править нечего.'; exit 1 }
            $cfgPath = Join-Path (Split-Path $proc.Path -Parent) 'appsettings.json'
            if (-not (Test-Path $cfgPath)) { "ОШИБКА: нет $cfgPath"; exit 1 }

            $json = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $old = $json.'{{key}}'
            $json | Add-Member -NotePropertyName '{{key}}' -NotePropertyValue {{jsonValue}} -Force
            $json | ConvertTo-Json -Depth 20 | Set-Content $cfgPath -Encoding UTF8
            "{{key}}: было '$old', стало '{{value}}' ($cfgPath)"

            # WatchdogHours без перевзвода задачи бессмысленен: срок уже зафиксирован в
            # расписании -Once, и правка файла на него не влияет (бэклог п.86).
            if ('{{key}}' -eq 'WatchdogHours') {
                $task = 'szdiag-watchdog-{{sz}}'
                $existing = Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
                if (-not $existing) { "ВНИМАНИЕ: задачи $task нет — новый срок применится при следующем открытии доступа." }
                else {
                    $action = ($existing.Actions | Select-Object -First 1)
                    $runAt = (Get-Date).AddHours([double]'{{value}}')
                    $trigger = New-ScheduledTaskTrigger -Once -At $runAt
                    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
                    Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger `
                        -Principal $principal -Force | Out-Null
                    "watchdog перевзведён на {0:yyyy-MM-dd HH:mm}" -f $runAt
                }
            } else {
                'Изменение вступит в силу при следующем открытии СЗ (агент читает конфиг при старте).'
            }
            """;
    }
}
