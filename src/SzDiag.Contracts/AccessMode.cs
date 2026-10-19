namespace SzDiag.Contracts;

/// <summary>Как хост добирается до клиента. Строки, а не enum: значение ездит по SignalR,
/// лежит в SQLite и приходит от агентов старых сборок как null.</summary>
public static class AccessMode
{
    /// <summary>Прямой SSH по адресу в общей сети — быстрее и не зависит от Cloudflare.
    /// Агент выбирает этот режим, когда нашёл hub broadcast'ом по локалке.</summary>
    public const string Direct = "direct";

    /// <summary>SSH через quick tunnel: у машины нет входящего порта, доступного хосту.</summary>
    public const string Tunnel = "tunnel";
}
