namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Вкладка инспектора. Обновляется только видимая — под нагрузкой exec-канал клиента
/// и так узкий (спека, «Источники данных GUI»).</summary>
public interface IInspectorTab
{
    string Title { get; }

    /// <summary>Как часто обновлять, пока вкладка видна; null — только при открытии и по кнопке.</summary>
    TimeSpan? Interval { get; }

    /// <summary>Обновить заново при смене ⚡N выбранной СЗ.</summary>
    bool RefreshOnReboot => false;

    /// <summary>Ошибки обрабатывает сама (показывает на вкладке) — наружу не бросает.</summary>
    Task RefreshAsync(string sz, CancellationToken ct);

    /// <summary>Выбрана другая СЗ — забыть данные прежней.</summary>
    void Clear();
}
