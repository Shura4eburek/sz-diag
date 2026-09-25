using System.Collections.ObjectModel;
using SzDiag.Claude;

namespace SzDiag.Desk.ViewModels;

/// <summary>События сессии → карточки ленты. Вызывать в UI-потоке.</summary>
public sealed class FeedBuilder(Action<string, bool> answer, Action restart)
{
    /// <summary>Сколько вывода держать в карточке: 200k символов exec-а в одном TextBlock
    /// подвешивают раскладку, а полный вывод остаётся в журнале сессии.</summary>
    public const int MaxOutputChars = 20_000;

    private readonly Dictionary<string, ToolFeedItem> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PermissionFeedItem> _permissions = new(StringComparer.Ordinal);

    public ObservableCollection<FeedItemViewModel> Items { get; } = new();

    public void Add(ClaudeEvent e)
    {
        switch (e)
        {
            case DeskUserMessage m:
                Items.Add(new UserFeedItem(m.Text) { At = m.At });
                break;
            case AssistantText { ParentToolUseId: null } t:
                Items.Add(new AssistantFeedItem(t.Text) { At = t.At });
                break;
            case ToolUse { ParentToolUseId: { } parent }:
                // Шаги субагента в ленту не льём — считаем на карточке вызвавшего инструмента.
                if (_tools.TryGetValue(parent, out var owner)) owner.SubSteps++;
                break;
            case ToolUse u:
                var card = new ToolFeedItem(u.Id, u.Name, ToolSummary.For(u.Name, u.Input)) { At = u.At };
                _tools[u.Id] = card;
                Items.Add(card);
                break;
            case ToolResult { ParentToolUseId: null } r when _tools.TryGetValue(r.ToolUseId, out var c):
                c.Status = r.Denied ? ToolStatus.Denied : r.IsError ? ToolStatus.Error : ToolStatus.Ok;
                c.Output = Clip(r.Text);
                if (c.At is { } start && r.At is { } end && end >= start) c.Duration = end - start;
                break;
            case TurnResult { Interrupted: true } ti:
                Items.Add(new NoteFeedItem("ход прерван") { At = ti.At });
                break;
            case TurnResult { IsError: true } te:
                Items.Add(new NoteFeedItem($"ход завершился ошибкой: {te.Text}") { At = te.At });
                break;
            case DeskNote n:
                Items.Add(new NoteFeedItem(n.Text) { At = n.At });
                break;
            case PermissionAsked p:
                var item = new PermissionFeedItem(p.RequestId, p.ToolName, ToolSummary.For(p.ToolName, p.Input),
                    ToolSummary.Details(p.ToolName, p.Input), answer) { At = p.At };
                _permissions[p.RequestId] = item;
                Items.Add(item);
                break;
            case PermissionAnswered a when _permissions.TryGetValue(a.RequestId, out var q):
                q.Allowed = a.Allowed;
                break;
            case ProcessCrashed x:
                Items.Add(new CrashFeedItem(
                    x.ExitCode is { } code ? $"claude завершился (код {code})" : "claude не запустился",
                    string.Join("\n", x.StderrTail), restart) { At = x.At });
                break;
            case UnknownEvent u:
                Items.Add(new RawFeedItem(u.Type, u.Raw) { At = u.At });
                break;
        }
    }

    public void ExpirePermissionsExcept(IEnumerable<string> liveIds)
    {
        var live = liveIds.ToHashSet(StringComparer.Ordinal);
        foreach (var (id, item) in _permissions)
            if (item.IsPending && !live.Contains(id)) item.Expire();
    }

    internal static string Clip(string text) => text.Length <= MaxOutputChars
        ? text
        : text[..MaxOutputChars] + $"\n[вывод обрезан: показано {MaxOutputChars} из {text.Length} символов]";
}
