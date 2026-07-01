namespace SzDiag.Agent;

/// <summary>Шаг набора тестов: type = "command" | "screenshot".</summary>
public sealed record TestStep(string Type, string Name, string? Run = null);
