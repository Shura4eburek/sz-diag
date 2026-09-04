namespace SzDiag.Contracts;

/// <summary>Запуск программы в ИНТЕРАКТИВНОЙ сессии пользователя, когда агент живёт под
/// SYSTEM (session 0) — GUI-приложения из `Start-Process` там не создают окна, а окно
/// показывать некому: `SessionId` уходит в 0.
///
/// Ритуал руками собирался трижды по разным заявкам (OCCT, TM5, SignalRGB — бэклог,
/// пункт без номера) одной и той же транзиентной scheduled-задачей
/// (`schtasks /create /tr "&lt;exe&gt;" /ru &lt;машина&gt;\&lt;юзер&gt; /rl highest /it /f`).
///
/// Две детали, добытые дорого (повтор на 111111, 03.09, бэклог п.220):
/// * <b>RunLevel Limited для GUI</b>, а не Highest: elevated-процесс своё окно не создаёт —
///   передаёт команду уже запущенному unelevated-процессу оболочки и молча выходит
///   (`LastTaskResult 0x1` при этом штатен). Highest нужен только приложениям, которым
///   реально требуется elevation для их собственной работы (SignalRGB — `OpenSCManager
///   Error 5` без него).
/// * <b>имя пользователя — из `Win32_ComputerSystem.UserName`</b>, не из `quser`: в session 0
///   `quser` отдаёт строку без маркера `&gt;`, наивный парсер (`-replace '^\s*&gt;?', ''`,
///   как в `run-in-session.ps1`) возвращает пустоту, и `Register-ScheduledTask` падает
///   `0x80070534` (SID не резолвится).</summary>
public static class InteractiveSessionRun
{
    /// <summary>Имя транзиентной задачи. Общий префикс — её видит уборка следов.</summary>
    public static string TaskName(string sz) => $"szdiag-app-{sz}";

    /// <summary>Скрипт запуска. Регистрирует задачу от имени активного интерактивного
    /// пользователя, запускает, ждёт появления процесса в его сессии, снимает задачу и
    /// печатает PID/сессию — либо честную ошибку, если окно так и не появилось.</summary>
    /// <param name="elevated">Highest вместо Limited — только когда приложению реально нужна
    /// elevation для своей работы (не для того, чтобы у него было окно).</param>
    /// <param name="waitSeconds">Сколько ждать появления процесса перед проверкой.</param>
    public static string BuildScript(string sz, string exe, string? args, bool elevated, int waitSeconds = 10)
    {
        var task = TaskName(sz);
        var runLevel = elevated ? "Highest" : "Limited";
        var exeEscaped = exe.Replace("'", "''");
        var argsEscaped = (args ?? "").Replace("'", "''");

        return $$"""
            $ErrorActionPreference = 'Stop'
            # Win32_ComputerSystem.UserName, а НЕ quser: в session 0 quser отдаёт строку без
            # маркера '>', наивный парсер возвращает пустоту, Register-ScheduledTask падает
            # 0x80070534 (бэклог п.220, повтор на 111111).
            $userName = (Get-CimInstance Win32_ComputerSystem).UserName
            if (-not $userName) { 'ОШИБКА: нет активного интерактивного пользователя — запускать некуда.'; exit 1 }

            $task = '{{task}}'
            Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
            $act = New-ScheduledTaskAction -Execute '{{exeEscaped}}' -Argument '{{argsEscaped}}'
            $pr  = New-ScheduledTaskPrincipal -UserId $userName -LogonType Interactive -RunLevel {{runLevel}}
            Register-ScheduledTask -TaskName $task -Action $act -Principal $pr -Force | Out-Null
            Start-ScheduledTask -TaskName $task
            Start-Sleep -Seconds {{waitSeconds}}

            $name = [IO.Path]::GetFileNameWithoutExtension('{{exeEscaped}}')
            # SessionId 0 = никто не видит это окно — тот самый молчаливый провал, ради
            # которого весь этот скрипт (бэклог п.220).
            $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -gt 0 })
            Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue

            if ($procs.Count -gt 0) {
                "OK: пользователь $userName, PID " + ($procs.Id -join ',') + ", session " + $procs[0].SessionId
            } else {
                "ОШИБКА: процесс $name не найден в интерактивной сессии через {{waitSeconds}} с (RunLevel {{runLevel}}, пользователь $userName) — окно не появилось"
                exit 1
            }
            """;
    }
}
