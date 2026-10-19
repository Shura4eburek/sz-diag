using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public sealed class FakeSzcliRunner : ISzcliRunner
{
    public List<IReadOnlyList<string>> Calls { get; } = new();
    public Func<IReadOnlyList<string>, SzcliResult> Respond { get; set; } = _ => new SzcliResult(0, "ok");

    /// <summary>Не null — вызов ждёт, пока тест его не отпустит.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public string? Location => @"C:\dist\host\szcli.cmd";

    public async Task<SzcliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Calls.Add(args);
        if (Gate is not null) await Gate.Task.WaitAsync(ct);
        return Respond(args);
    }
}
