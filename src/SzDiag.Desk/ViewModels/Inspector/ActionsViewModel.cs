using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Действия по СЗ — те же команды szcli, что в терминале (спека, «Действия инспектора»;
/// почему подпроцессом — см. <see cref="SzcliRunner"/>). Разрушительное (close, unfreeze) — только
/// после подтверждения. Пока идёт одна команда, остальные не запускаются.</summary>
public sealed partial class ActionsViewModel(ISzcliRunner szcli) : ObservableObject, IInspectorTab
{
    private string? _sz;
    private IReadOnlyList<string>? _pendingArgs;
    private CancellationTokenSource? _cts;

    public string Title => "Действия";
    public TimeSpan? Interval => null;

    [ObservableProperty] private string _diagSections = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(TestRunCommand))] private string _testConfig = "";
    [ObservableProperty] private string _testFilter = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NoteCommand))] private string _noteText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CloseForceCommand))] private string _closeReason = "";
    [ObservableProperty] private string _log = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _running;
    [ObservableProperty] private string? _pendingConfirm;
    [ObservableProperty] private int? _lastExitCode;

    public bool IsIdle => !Running;

    public Task RefreshAsync(string sz, CancellationToken ct)
    {
        _sz = sz;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task DiagRun()
        => Run(Args("diag", "run").Concat(Split(DiagSections)).ToList());

    private bool CanTestRun() => TestConfig.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanTestRun))]
    private Task TestRun()
    {
        var args = Args("test", "run").ToList();
        if (TestFilter.Trim() is { Length: > 0 } filter) args.Add(filter);
        args.Add("--config");
        args.Add(TestConfig.Trim());
        return Run(args);
    }

    [RelayCommand]
    private Task Freeze() => Run(Args("freeze").ToList());

    [RelayCommand]
    private void Unfreeze()
        => Ask($"Снять заморозку Windows Update с СЗ {_sz}? Обновления снова смогут перезагрузить машину.",
            Args("unfreeze").ToList());

    private bool CanNote() => NoteText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanNote))]
    private async Task Note()
    {
        await Run(Args("note").Append(NoteText.Trim()).ToList());
        if (LastExitCode == 0) NoteText = "";
    }

    [RelayCommand]
    private Task SzFetch() => Run(Args("sz", "fetch").ToList());

    [RelayCommand]
    private void Close()
        => Ask($"Закрыть СЗ {_sz}? Доступ на клиенте откатится, сессия Claude уйдёт в архив.", Args("close").ToList());

    private bool CanCloseForce() => CloseReason.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanCloseForce))]
    private void CloseForce()
        => Ask($"Закрыть СЗ {_sz} принудительно, мимо защит close? Причина уйдёт в журнал.",
            Args("close").Concat(new[] { "--force", CloseReason.Trim() }).ToList());

    [RelayCommand]
    private Task Confirm()
    {
        var args = _pendingArgs;
        _pendingArgs = null;
        PendingConfirm = null;
        return args is null ? Task.CompletedTask : Run(args);
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        _pendingArgs = null;
        PendingConfirm = null;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void Ask(string question, IReadOnlyList<string> args)
    {
        if (_sz is null) return;
        _pendingArgs = args;
        PendingConfirm = question;
    }

    /// <summary>Номер СЗ вставляется сразу после подкоманды: `szcli diag run &lt;сз&gt;`,
    /// `szcli sz fetch &lt;сз&gt;`, `szcli close &lt;сз&gt;`.</summary>
    private IEnumerable<string> Args(params string[] command) => command.Append(_sz ?? "");

    private static IEnumerable<string> Split(string s)
        => s.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task Run(IReadOnlyList<string> args)
    {
        if (_sz is null || Running) return;
        Running = true;
        _cts = new CancellationTokenSource();
        var shown = "> szcli " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        Log = shown + "\n…";
        try
        {
            var r = await szcli.RunAsync(args, _cts.Token);
            LastExitCode = r.ExitCode;
            Log = $"{shown}\n{r.Output}\n[код выхода {r.ExitCode}]";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Running = false;
        }
    }

    public void Clear()
    {
        _sz = null;
        _pendingArgs = null;
        PendingConfirm = null;
        Log = "";
        LastExitCode = null;
    }
}
