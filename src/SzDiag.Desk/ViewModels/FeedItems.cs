using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SzDiag.Desk.ViewModels;

public abstract class FeedItemViewModel : ObservableObject
{
    public DateTimeOffset? At { get; init; }
}

public sealed class UserFeedItem(string text) : FeedItemViewModel
{
    public string Text { get; } = text;
}

public sealed class AssistantFeedItem(string text) : FeedItemViewModel
{
    public string Text { get; } = text;
}

/// <summary>Серая системная строка: события машины, прерывание, архив.</summary>
public sealed class NoteFeedItem(string text) : FeedItemViewModel
{
    public string Text { get; } = text;
}

public sealed class RawFeedItem(string type, string raw) : FeedItemViewModel
{
    public string Title { get; } = $"raw · {type}";
    public string Raw { get; } = raw;
}

public sealed class CrashFeedItem(string title, string details, Action restart) : FeedItemViewModel
{
    public string Title { get; } = title;
    public string Details { get; } = details;
    public IRelayCommand RestartCommand { get; } = new RelayCommand(restart);
}

public enum ToolStatus { Running, Ok, Error, Denied }

/// <summary>Свёрнутая моно-карточка вызова инструмента `✓ команда · 12с`, раскрывается в вывод.</summary>
public sealed partial class ToolFeedItem : FeedItemViewModel
{
    public ToolFeedItem(string id, string name, string summary)
    {
        Id = id;
        Name = name;
        Summary = summary;
    }

    public string Id { get; }
    public string Name { get; }
    public string Summary { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Header))] private ToolStatus _status = ToolStatus.Running;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Header))] private TimeSpan? _duration;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Header))] private int _subSteps;
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _isExpanded;

    public string Header
    {
        get
        {
            var glyph = Status switch
            {
                ToolStatus.Ok => "✓",
                ToolStatus.Error => "✗",
                ToolStatus.Denied => "⊘",
                _ => "…",
            };
            var head = $"{glyph} {Name} · {Summary}";
            if (Duration is { } d) head += $" · {FormatDuration(d)}";
            if (SubSteps > 0) head += $" · субагент: {SubSteps}";
            return head;
        }
    }

    internal static string FormatDuration(TimeSpan d)
        => d.TotalSeconds < 60 ? $"{Math.Max(0, (int)d.TotalSeconds)}с" : $"{(int)d.TotalMinutes}м {d.Seconds:00}с";

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>Оранжевая карточка разрешения. Ответ уходит в брокер, а «разрешено/отклонено»
/// ставится по событию PermissionAnswered — карточка показывает то, что реально произошло.</summary>
public sealed partial class PermissionFeedItem : FeedItemViewModel
{
    private readonly Action<string, bool> _answer;
    private bool _expired;

    public PermissionFeedItem(string requestId, string toolName, string summary, string details,
        Action<string, bool> answer)
    {
        RequestId = requestId;
        ToolName = toolName;
        Summary = summary;
        Details = details;
        _answer = answer;
    }

    public string RequestId { get; }
    public string ToolName { get; }
    public string Summary { get; }

    /// <summary>Полный вход инструмента — то, что оператор на самом деле разрешает.</summary>
    public string Details { get; }

    public string Title => $"Разрешить {ToolName}?";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPending), nameof(ResultText))] private bool? _allowed;

    public bool IsPending => Allowed is null;

    public string ResultText => _expired
        ? "запрос истёк — Desk перезапускался"
        : Allowed switch { true => "разрешено", false => "отклонено", null => "" };

    /// <summary>Запрос из прошлого запуска Desk: брокер о нём уже не знает.</summary>
    internal void Expire()
    {
        _expired = true;
        Allowed = false;
    }

    [RelayCommand]
    private void Allow() => _answer(RequestId, true);

    [RelayCommand]
    private void Deny() => _answer(RequestId, false);
}
