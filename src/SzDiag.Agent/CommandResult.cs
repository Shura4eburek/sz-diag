namespace SzDiag.Agent;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr);
