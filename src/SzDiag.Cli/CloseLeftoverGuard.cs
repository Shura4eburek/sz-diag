using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli close` не должен молча закрывать СЗ, пока на клиенте остаются файлы,
/// доставленные `push` (инструменты, рабочие папки рецептов).
///
/// Регрессия (бэклог п.158, СЗ 160306): после закрытия на машине осталось 101 МБ наших
/// бинарей (`tools\prime95`, `tools\lhmmon`) и рабочая папка `C:\OCCT` — `close` предлагал
/// «проверить остатки», но сам их не проверял, и на следующей заявке об этом просто забыли.
/// Модель угроз требует обратного: весь наш след откатывается без остатка.</summary>
public static class CloseLeftoverGuard
{
    /// <summary>Среди остатков есть то, что оставил именно наш инструментарий (доставленные
    /// push'ом тулы или их рабочие каталоги) — а не, например, чужая задача планировщика.
    /// Только эти пункты блокируют close: он не заменяет `client info` целиком, а ловит
    /// ровно то, для чего заводился (бэклог п.158).</summary>
    public static bool HasDeliveredFiles(IReadOnlyList<string> leftovers)
        => leftovers.Any(IsBlocking);

    /// <summary>Блокирует close только «доставленный инструмент …» и рабочие каталоги
    /// РЕЦЕПТОВ (<see cref="ClientTraces.RecipeWorkDirs"/>, напр. C:\OCCT). Собственные
    /// служебные каталоги (`ProgramData\szdiag\jobs`/`sensors`) НЕ блокируют: они появляются
    /// после ЛЮБОГО `exec --detach`/`sensors start` — раньше любое слово «каталог» в остатках
    /// блокировало close почти всегда, приучая оператора к обходу защиты (review W2 I-2).</summary>
    public static bool IsBlocking(string leftover)
        => leftover.Contains("доставленный инструмент", StringComparison.OrdinalIgnoreCase)
           || ClientTraces.RecipeWorkDirs.Any(d => leftover.Contains(d, StringComparison.OrdinalIgnoreCase));
}
