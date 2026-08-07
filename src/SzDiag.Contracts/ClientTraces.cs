namespace SzDiag.Contracts;

/// <summary>Следы, которые мы оставляем на клиентской машине, и их уборка.
///
/// Три живых боли в одном месте:
/// * **п.88** — `lhmmon` грузит kernel-драйвер под именем **`R0lhmmon`** (не `lhmmon`!), из-за
///   чего `.sys` держится, папка не удаляется, а в реестре навсегда остаётся
///   `HKLM\SYSTEM\CurrentControlSet\Services\R0lhmmon`;
/// * **п.56** — от прогонов 27–28.07 на 160306 остались `iotest.bin` на 12 ГБ, CSV сенсоров и
///   задачи `szdiag-iostress`/`szdiag-lhmmon` в состоянии Ready;
/// * **п.99** — задача `szdiag-lhmmon` **без номера СЗ**: из имени не понять, чья она, и
///   уборка по маске `szdiag-*-<СЗ>` её не находит.
///
/// Инвариант из CLAUDE.md — «доступ откатывается без следов» — распространяется и на это.</summary>
public static class ClientTraces
{
    /// <summary>Префикс всех наших задач. Единое место, чтобы имена не расходились по коду
    /// и рецептам (п.99).</summary>
    public const string TaskPrefix = "szdiag";

    /// <summary>Имя задачи по единому правилу `szdiag-<роль>-<СЗ>`. Задача без номера СЗ —
    /// это хвост, которого никто не найдёт.</summary>
    public static string TaskName(string role, string sz) => $"{TaskPrefix}-{role}-{sz}";

    /// <summary>Драйверы/сервисы, которые оставляют наши инструменты. Имя сервиса у LHM
    /// начинается с `R0` — по «очевидному» `lhmmon` уборка промахивалась (п.88).</summary>
    public static readonly string[] ToolServices = { "R0lhmmon", "WinRing0_1_2_0", "R0OCCT" };

    /// <summary>Наши временные каталоги на клиенте: вывод фоновых задач и CSV наблюдателя.
    /// Всё это заведомо наше — чистится без вопросов.</summary>
    public static readonly string[] TempDirs =
    {
        @"C:\ProgramData\szdiag\jobs",
        @"C:\ProgramData\szdiag\sensors",
    };

    /// <summary>Что осталось на машине: задачи с нашим префиксом (в том числе безымянные, без
    /// номера СЗ), загруженные драйверы инструментов, размеры наших каталогов и крупные файлы
    /// прогонов. Печатает строки `key=value`, чтобы разбирать одним парсером.</summary>
    public static string BuildInventoryScript()
    {
        var services = string.Join(",", ToolServices.Select(s => $"'{s}'"));
        var dirs = string.Join(",", TempDirs.Select(d => $"'{d}'"));
        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Get-ScheduledTask | Where-Object { $_.TaskName -like '{{TaskPrefix}}*' } |
                ForEach-Object { 'task:' + $_.TaskName + '=' + $_.State }
            foreach ($svc in @({{services}})) {
                $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $svc
                $present = Test-Path $key
                $running = (Get-Service -Name $svc -ErrorAction SilentlyContinue).Status
                'service:' + $svc + '=' + $(if ($present) { "$running/registered" } else { 'none' })
            }
            foreach ($dir in @({{dirs}})) {
                if (Test-Path $dir) {
                    $size = (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue |
                        Measure-Object -Property Length -Sum).Sum
                    'dir:' + $dir + '=' + [math]::Round(($size / 1MB), 1)
                } else { 'dir:' + $dir + '=none' }
            }
            # Крупные артефакты прогонов (iotest.bin на 12 ГБ и подобное) — только показываем:
            # удалять чужие файлы по маске нельзя, решение за оператором.
            Get-ChildItem 'C:\ProgramData\szdiag' -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Length -gt 500MB } |
                ForEach-Object { 'big:' + $_.FullName + '=' + [math]::Round(($_.Length / 1GB), 1) }
            """;
    }

    /// <summary>Уборка: снять задачи с нашим префиксом, выгрузить и удалить драйверы
    /// инструментов, вычистить наши временные каталоги.</summary>
    /// <param name="keepTasks">Задачи, которые снимать НЕЛЬЗЯ (текущая сессия: sshd, watchdog,
    /// автостарт) — иначе уборка обрубит доступ сама себе.</param>
    public static string BuildCleanupScript(IReadOnlyList<string>? keepTasks = null)
    {
        var keep = string.Join(",", (keepTasks ?? Array.Empty<string>()).Select(t => $"'{t}'"));
        var services = string.Join(",", ToolServices.Select(s => $"'{s}'"));
        var dirs = string.Join(",", TempDirs.Select(d => $"'{d}'"));
        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $keep = @({{keep}})
            Get-ScheduledTask | Where-Object { $_.TaskName -like '{{TaskPrefix}}*' -and $keep -notcontains $_.TaskName } |
                ForEach-Object {
                    Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
                    'снята задача: ' + $_.TaskName
                }
            foreach ($svc in @({{services}})) {
                if (Test-Path ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $svc)) {
                    & sc.exe stop $svc | Out-Null
                    & sc.exe delete $svc | Out-Null
                    'снят драйвер: ' + $svc
                }
            }
            foreach ($dir in @({{dirs}})) {
                if (Test-Path $dir) {
                    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
                    'вычищено: ' + $dir
                }
            }
            'cleanup-done'
            """;
    }

    /// <summary>Разбор вывода инвентаря в список проблем. Пустой список — следов нет.</summary>
    public static IReadOnlyList<string> FindLeftovers(string inventoryStdout)
    {
        var problems = new List<string>();
        foreach (var raw in (inventoryStdout ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var sep = line.IndexOf('=');
            if (sep <= 0) continue;
            var key = line[..sep];
            var value = line[(sep + 1)..].Trim();

            if (key.StartsWith("task:", StringComparison.OrdinalIgnoreCase))
            {
                var name = key["task:".Length..];
                var orphan = !name.Any(char.IsDigit) ? " (без номера СЗ — чей хвост, неизвестно)" : "";
                problems.Add($"задача {name}: {value}{orphan}");
            }
            else if (key.StartsWith("service:", StringComparison.OrdinalIgnoreCase)
                     && !value.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"драйвер {key["service:".Length..]}: {value}");
            }
            else if (key.StartsWith("big:", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"крупный файл {key["big:".Length..]}: {value} ГБ");
            }
        }
        return problems;
    }
}
