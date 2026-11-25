namespace SzDiag.Contracts;

/// <summary>Адрес узла ZeroTier — всегда 10 hex. libzt (<c>Node.IdString</c>) отдаёт его БЕЗ
/// ведущих нулей: на первом живом заезде агент прислал <c>e932809aa</c>, а контроллер знал узел
/// как <c>0e932809aa</c> — hub не смог бы удалить его при close, а сторож посчитал бы узел живой
/// СЗ чужим и выкинул клиента из сети. Нормализуем на обоих концах.</summary>
public static class ZtNodeId
{
    public const int Length = 10;

    /// <summary>Нижний регистр, ведущие нули до 10 символов. null — пусто или не hex/длиннее 10.</summary>
    public static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var s = id.Trim().ToLowerInvariant();
        if (s.Length > Length || !s.All(Uri.IsHexDigit)) return null;
        return s.PadLeft(Length, '0');
    }
}
