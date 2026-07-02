namespace SzDiag.Kb;

public enum TestStepKind { Command, Screenshot, App }

/// <summary>Результат одного шага прогона.</summary>
public sealed record TestStepResult(
    string Name,
    TestStepKind Kind,
    string? Command = null,
    string? Output = null,
    int? ExitCode = null,
    string? Error = null,
    string? ScreenshotFile = null);
