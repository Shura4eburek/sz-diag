namespace SzDiag.Agent;

/// <summary>Ярлык «Закрыть доступ SzDiag» на общем рабочем столе.
///
/// Боль (бэклог п.87, СЗ 160467): после ребута автостарт-задача поднимает `agent.exe --resume`
/// под SYSTEM — агент работает, СЗ снова online, но **консоли у него нет** (сессия 0, до
/// логина). А оба локальных пути отката завязаны на окно: клавиша `C` и перехват закрытия
/// консоли. Человек сидит за клиентской машиной, видит открытый доступ и снять его ничем не
/// может: в трее пусто, окна нет. Остаётся `szcli close` с хоста или ждать watchdog.
///
/// Ярлык ведёт на .cmd, который перезапускает агента с `--revert … --force` от админа (UAC):
/// откат обязан требовать прав, иначе его сможет дёрнуть любой процесс на машине.</summary>
public static class LocalRevertShortcut
{
    /// <summary>Общий рабочий стол — виден любому вошедшему пользователю.</summary>
    public static string DesktopDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

    public static string ShortcutPath(string sz) => Path.Combine(DesktopDir, FileName(sz));

    public static string FileName(string sz) => $"Закрыть доступ SzDiag (СЗ {sz}).lnk";

    /// <summary>Путь .cmd рядом со `state.json` — там же, где остальные следы сессии.</summary>
    public static string ScriptPath(string statePath, string sz)
        => Path.Combine(Path.GetDirectoryName(statePath)!, $"revert-{sz}.cmd");

    /// <summary>Содержимое .cmd: поднимает агента с --revert от имени администратора.
    /// `--force` обязателен: это осознанное действие человека, а не срабатывание watchdog,
    /// и метка живости агента тут роли не играет (см. <see cref="AccessLiveness"/>).</summary>
    public static string BuildScript(string exePath, string statePath) =>
        "@echo off\r\n"
        // chcp 65001 — потому что сам файл пишется в UTF-8: кодовые страницы вроде 866 в
        // .NET 8 без CodePagesEncodingProvider недоступны, а русский текст в cmd без смены
        // страницы превращается в кракозябры.
        + "chcp 65001 >nul\r\n"
        + "echo Закрываю доступ SzDiag и откатываю изменения...\r\n"
        + $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"Start-Process -FilePath '{exePath}' "
        + $"-ArgumentList '--revert','{statePath}','--force' -Verb RunAs -Wait\"\r\n"
        + "echo Готово. Проверить можно так: доступ закрыт, если порт 22 больше не слушается.\r\n"
        + "pause\r\n";

    /// <summary>PowerShell создания ярлыка (WScript.Shell — штатный способ без сторонних
    /// библиотек).</summary>
    public static string BuildCreateCommand(string shortcutPath, string scriptPath) =>
        "$w = New-Object -ComObject WScript.Shell; " +
        $"$s = $w.CreateShortcut('{shortcutPath.Replace("'", "''")}'); " +
        $"$s.TargetPath = '{scriptPath.Replace("'", "''")}'; " +
        $"$s.WorkingDirectory = '{Path.GetDirectoryName(scriptPath)!.Replace("'", "''")}'; " +
        "$s.Description = 'Снять удалённый доступ сервисного центра и откатить изменения'; " +
        "$s.IconLocation = 'shell32.dll,110'; " +
        "$s.Save()";

    /// <summary>Положить ярлык и .cmd. Возвращает false, если не получилось (нет общего
    /// рабочего стола, нет прав) — это не повод срывать открытие доступа.</summary>
    public static bool Create(IPowerShellRunner ps, string sz, string exePath, string statePath)
    {
        try
        {
            var script = ScriptPath(statePath, sz);
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            // UTF-8 без BOM: BOM в .cmd cmd.exe печатает первой строкой как мусор, а
            // кодовая страница переключается самим скриптом (chcp 65001).
            File.WriteAllText(script, BuildScript(exePath, statePath),
                new System.Text.UTF8Encoding(false));

            ps.Run(BuildCreateCommand(ShortcutPath(sz), script), throwOnError: false);
            return File.Exists(ShortcutPath(sz));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Убрать ярлык и .cmd — след на машине клиента оставаться не должен.</summary>
    public static void Remove(string sz, string statePath)
    {
        try { if (File.Exists(ShortcutPath(sz))) File.Delete(ShortcutPath(sz)); } catch { }
        try
        {
            var script = ScriptPath(statePath, sz);
            if (File.Exists(script)) File.Delete(script);
        }
        catch { }
    }
}
