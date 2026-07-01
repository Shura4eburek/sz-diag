namespace SzDiag.Agent;

/// <summary>Выполняет команды теста через PowerShell (обёртка PowerShellRunner).</summary>
public sealed class PowerShellCommandExecutor : ICommandExecutor
{
    private readonly PowerShellRunner _ps;
    public PowerShellCommandExecutor(PowerShellRunner ps) => _ps = ps;

    public CommandResult Run(string command)
    {
        var r = _ps.Run(command, throwOnError: false);
        return new CommandResult(r.ExitCode, r.StdOut, r.StdErr);
    }
}
