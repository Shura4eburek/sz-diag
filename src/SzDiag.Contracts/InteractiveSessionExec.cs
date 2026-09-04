using System.Text;

namespace SzDiag.Contracts;

/// <summary>`szcli exec <СЗ> --in-session "<script>"` — прогнать произвольный PowerShell в
/// ИНТЕРАКТИВНОЙ сессии пользователя, а не в session 0 агента.
///
/// Родня <see cref="InteractiveSessionRun"/> (запуск GUI-приложения), но с ключевым отличием:
/// сюда нужен НЕ ТОЛЬКО факт запуска, а сам вывод скрипта — вернуть его через exec-канал так же,
/// как обычный `exec`. Внутренний скрипт кодируется base64 (та же схема, что и
/// `-EncodedCommand`), пишется во временный `.ps1`, запускается транзиентной задачей от имени
/// активного пользователя с перенаправлением вывода в файл, и после завершения задачи файл
/// читается и возвращается как результат.
///
/// Повторяющаяся боль (бэклог п.220): после ребута агент поднимается под
/// `NT AUTHORITY\СИСТЕМА` в session 0, откуда `Start-Process`/скриншот/GUI ломаются молча.
/// Простейшая просьба «открой диск в проводнике на клиенте» каждый раз выливалась в написание
/// новой транзиентной задачи с нуля.</summary>
public static class InteractiveSessionExec
{
    public static string TaskName(string sz) => $"szdiag-inexec-{sz}";
    private static string WorkDir => @"C:\ProgramData\szdiag";
    private static string ScriptPath(string sz) => $@"{WorkDir}\in-session-{sz}.ps1";
    private static string OutPath(string sz) => $@"{WorkDir}\in-session-{sz}.out.txt";

    /// <summary>Скрипт-обёртка, которую агент выполняет у себя (сам всё ещё в session 0):
    /// раскладывает внутренний скрипт во временный файл, запускает его транзиентной задачей
    /// от активного пользователя, ждёт завершения и возвращает содержимое файла вывода.</summary>
    /// <param name="innerScript">То, что реально нужно выполнить — присланное через
    /// `exec --in-session`.</param>
    /// <param name="waitSeconds">Сколько ждать завершения задачи, прежде чем сдаться.</param>
    public static string BuildScript(string sz, string innerScript, int waitSeconds = 60)
    {
        var task = TaskName(sz);
        var script = ScriptPath(sz);
        var outFile = OutPath(sz);
        var encodedInner = Convert.ToBase64String(Encoding.Unicode.GetBytes(innerScript ?? ""));

        return $$"""
            $ErrorActionPreference = 'Stop'
            $userName = (Get-CimInstance Win32_ComputerSystem).UserName
            if (-not $userName) { 'ОШИБКА: нет активного интерактивного пользователя — выполнять негде.'; exit 1 }

            New-Item -ItemType Directory -Force -Path '{{WorkDir}}' | Out-Null
            $bytes = [Convert]::FromBase64String('{{encodedInner}}')
            [IO.File]::WriteAllText('{{script}}', [Text.Encoding]::Unicode.GetString($bytes), (New-Object Text.UTF8Encoding($true)))
            Remove-Item '{{outFile}}' -ErrorAction SilentlyContinue

            $task = '{{task}}'
            Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
            $inner = 'powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{{script}}" *> "{{outFile}}"'
            $act = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c $inner"
            $pr  = New-ScheduledTaskPrincipal -UserId $userName -LogonType Interactive -RunLevel Limited
            Register-ScheduledTask -TaskName $task -Action $act -Principal $pr -Force | Out-Null
            Start-ScheduledTask -TaskName $task

            $deadline = (Get-Date).AddSeconds({{waitSeconds}})
            while ((Get-Date) -lt $deadline) {
                $st = (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue).State
                if ($st -eq 'Ready') { break }
                Start-Sleep -Milliseconds 500
            }
            Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue

            if (Test-Path '{{outFile}}') {
                "пользователь: $userName"
                Get-Content '{{outFile}}' -Raw
            } else {
                "ОШИБКА: результат не появился за {{waitSeconds}} с (пользователь $userName) — интерактивная задача не завершилась"
                exit 1
            }
            """;
    }
}
