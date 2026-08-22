using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>Маппинг результата exec в exit code szcli: успех, exit N внутри скрипта и отказ
/// агента раньше все давали $LASTEXITCODE = 0 — поверх exec нельзя было автоматизировать
/// (бэклог п.103). Коды: 0 — успех; N — код скрипта как есть; 3 — отказ/ошибка агента;
/// 4 — скрипт убит по таймауту.</summary>
public static class ExecExitCode
{
    public const int AgentFailure = 3;
    public const int Timeout = 4;

    public static int From(ExecResult result)
    {
        if (result.TimedOut) return Timeout;
        if (result.ExitCode < 0) return AgentFailure;   // отказ агента, ошибка запуска
        return result.ExitCode;
    }

    public static int FromStatus(ExecJobStatus status)
    {
        if (!string.IsNullOrEmpty(status.Error) && status.ExitCode is null && !status.Running)
            return AgentFailure;
        if (status.Running) return 0;
        return status.ExitCode is { } code ? (code < 0 ? AgentFailure : code) : 0;
    }
}
