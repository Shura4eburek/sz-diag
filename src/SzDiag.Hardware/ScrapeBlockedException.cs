namespace SzDiag.Hardware;

/// <summary>TPU вернул bot-challenge вместо страницы. Резолвер ловит и деградирует мягко.</summary>
public sealed class ScrapeBlockedException : Exception
{
    public ScrapeBlockedException(string message) : base(message) { }
}
