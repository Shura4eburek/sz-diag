namespace SzDiag.Contracts;

/// <summary>Hub → агент: скачать инструмент с hub и разложить у себя.
///
/// Доставка через SMB принципиально ненадёжна: на 160306 она встала колом почти на час
/// (`System error 67` при открытом порте 445 и верной шаре), а на 160467 повторилась без
/// всякого антивируса — виноват kill switch VPN на хосте, режущий локальный трафик через WFP.
/// HTTP-канал до hub при этом работает всегда: по нему агент и так качает свои обновления
/// (бэклог п.1). Направление то же, что в модели угроз: **клиент тянет с хоста**.</summary>
/// <param name="Tool">Имя инструмента (папка в `client-tools`), напр. <c>occt</c>.</param>
public sealed record PushRequest(string Sz, string RequestId, string Tool);

/// <summary>Агент → hub: чем закончилась доставка.</summary>
/// <param name="TargetDir">Куда легло на клиенте (это же надо знать, чтобы запускать).</param>
/// <param name="Downloaded">Скачано файлов.</param>
/// <param name="Skipped">Пропущено — уже лежали с тем же sha256 (повторный push дёшев).</param>
/// <param name="Bytes">Сколько реально скачано.</param>
/// <param name="Error">Не пусто — доставка не удалась целиком.</param>
public sealed record PushResult(
    string RequestId,
    string TargetDir,
    int Downloaded,
    int Skipped,
    long Bytes,
    string? Error = null);

/// <summary>Один файл инструмента в манифесте раздачи.</summary>
/// <param name="Path">Путь относительно папки инструмента (с прямыми слэшами).</param>
public sealed record ToolFile(string Path, long Size, string Sha256);

/// <summary>Состав инструмента: hub отдаёт манифест, агент по нему качает файлы.</summary>
public sealed record ToolManifest(string Tool, IReadOnlyList<ToolFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);
}

/// <summary>Краткая карточка инструмента для `szcli push --list`.</summary>
public sealed record ToolInfo(string Name, int Files, long Bytes);

/// <summary>Тело HTTP-запроса CLI → hub.</summary>
public sealed record PushCommandRequest(string Tool);

/// <summary>Маршруты раздачи инструментов (HTTP, под тем же `X-SzDiag-Token`, что и пакет агента).</summary>
public static class ToolRoutes
{
    public const string Prefix = "/tools";
    public const string ListRoute = "/tools/list";

    /// <summary>Манифест инструмента: <c>/tools/{tool}/manifest</c>.</summary>
    public static string Manifest(string tool) => $"{Prefix}/{tool}/manifest";

    /// <summary>Файл инструмента: <c>/tools/{tool}/file?path=...</c>.</summary>
    public static string File(string tool, string relativePath)
        => $"{Prefix}/{tool}/file?path={Uri.EscapeDataString(relativePath)}";
}

/// <summary>Лимиты доставки.</summary>
public static class PushLimits
{
    /// <summary>Сколько hub ждёт агента. OCCT — почти 300 МБ, по локальной сети это минуты,
    /// а под нагрузкой на клиенте и дольше.</summary>
    public const int TimeoutSeconds = 1800;
}
