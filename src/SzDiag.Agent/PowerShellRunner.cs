using System.Diagnostics;

namespace SzDiag.Agent;

public sealed record PsResult(int ExitCode, string StdOut, string StdErr);

/// <summary>PowerShell-команда не уложилась в отведённый таймаут — процесс убит.</summary>
public sealed class PowerShellTimeoutException : Exception
{
    public PowerShellTimeoutException(string message) : base(message) { }
}

/// <summary>Абстракция запуска PowerShell — тест-шов для оркестрации системных вызовов.</summary>
public interface IPowerShellRunner
{
    PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null);
}

/// <summary>Запуск PowerShell-команд. Кидает при ненулевом коде, если throwOnError.</summary>
public sealed class PowerShellRunner : IPowerShellRunner
{
    private readonly bool _utf8;

    /// <param name="utf8">Заставлять дочерний PowerShell писать stdout/stderr в UTF-8.
    /// На обычной винде — обязательно (иначе кириллица уезжает в cp866: кракозябры в
    /// diag.md и в выводе exec). В WinPE — <b>нельзя</b>: там консоль на cp437, вывод
    /// перенаправлен, и присвоение <c>[Console]::OutputEncoding</c> вешает powershell.exe
    /// намертво (ловили на СЗ 159948 — exec молчал до таймаута даже на скрипте из одной
    /// строки). Дефолт определяется средой.</param>
    public PowerShellRunner(bool? utf8 = null) => _utf8 = utf8 ?? !WinPeEnvironment.IsWinPe;

    /// <summary>Порог для файла-фоллбэка: -EncodedCommand — это 2,67 символа аргумента на
    /// символ скрипта, а лимит командной строки Windows — 32 767. Секция whea (~13 КБ
    /// исходника) дважды падала на живых заявках с «имя файла слишком длинное» (п.101/196).</summary>
    private const int MaxEncodedCommandChars = 30_000;

    public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
    {
        // Скрипт передаём через -EncodedCommand (base64 UTF-16LE), а НЕ через stdin
        // `-Command -`: последний в PowerShell 5.1 обрывает многострочные конвейеры
        // (строка с хвостовым | или , рвётся) — до вывода доходит лишь первая строка,
        // из-за чего все секции RunDiag на живой машине выходили пустыми. EncodedCommand
        // исполняет скрипт как единое целое. $ProgressPreference убирает CLIXML-шум
        // прогресса из stderr.
        // Кодировка вывода задаётся здесь, а не строкой в пользовательском скрипте: при
        // перенаправлённом stdout PowerShell 5.1 кодирует вывод в [Console]::OutputEncoding,
        // а у headless-агента (автостарт-задача под SYSTEM, консоли нет) это OEM-страница
        // клиента — кириллица приезжает мусором. Прибиваем UTF-8 с обеих сторон: в шапке
        // скрипта (как пишет дочерний процесс) и в StandardOutputEncoding (как читаем мы).
        var prefix = _utf8
            ? "[Console]::OutputEncoding=[Text.Encoding]::UTF8;$OutputEncoding=[Text.Encoding]::UTF8;\n"
            : string.Empty;
        // param(...) обязан быть первым выражением скрипта, а шапка выше его сдвигает —
        // любой рецепт с параметрами падал с CommandNotFoundException и шёл дальше мимо
        // аргументов (п.102/168/189). Такой скрипт заворачиваем в &{}: внутри scriptblock
        // param снова первый, а exit по-прежнему завершает процесс своим кодом.
        var body = StartsWithParamBlock(script) ? "& {\n" + script + "\n}" : script;
        var full = prefix + "$ProgressPreference='SilentlyContinue';\n" + body;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(full));

        // Длинный скрипт не влезает в командную строку — уводим во временный .ps1 (-File).
        // UTF-8 строго с BOM: без него PowerShell 5.1 читает файл в ANSI и жуёт кириллицу.
        string? tempFile = null;
        var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";
        if (encoded.Length > MaxEncodedCommandChars)
        {
            tempFile = Path.Combine(Path.GetTempPath(), $"szdiag-ps-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(tempFile, full, new System.Text.UTF8Encoding(true));
            arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempFile}\"";
        }
        try
        {
            return RunProcess(arguments, script, throwOnError, timeout);
        }
        finally
        {
            if (tempFile is not null)
                try { File.Delete(tempFile); } catch { /* занят антивирусом — мусор в %TEMP% не критичен */ }
        }
    }

    /// <summary>Скрипт начинается с param-блока (комментарии и пустые строки не в счёт)?</summary>
    private static bool StartsWithParamBlock(string script)
        => System.Text.RegularExpressions.Regex.IsMatch(script,
            @"^\s*(?:(?:#[^\r\n]*|<#[\s\S]*?#>)\s*)*param\s*\(",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private PsResult RunProcess(string arguments, string script, bool throwOnError, TimeSpan? timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (_utf8)
        {
            psi.StandardOutputEncoding = new System.Text.UTF8Encoding(false);
            psi.StandardErrorEncoding = new System.Text.UTF8Encoding(false);
        }
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();   // дочерним процессам — сразу EOF на stdin, чтобы не висли

        // Асинхронное чтение запущено ДО WaitForExit: синхронный ReadToEnd() блокируется
        // до EOF, которое наступает только при завершении процесса — с ним таймаут
        // никогда бы не сработал (мы бы зависли на самом чтении, а не дошли до ожидания).
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var exited = timeout is null
            ? p.WaitForExit(Timeout.Infinite)
            : p.WaitForExit((int)timeout.Value.TotalMilliseconds);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* уже мог сам завершиться — гонка */ }
            throw new PowerShellTimeoutException($"PowerShell не уложился в таймаут {timeout}: {script}");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (throwOnError && p.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell завершился с кодом {p.ExitCode}: {stderr}");

        return new PsResult(p.ExitCode, stdout, stderr);
    }
}
