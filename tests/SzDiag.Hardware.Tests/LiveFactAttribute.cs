namespace SzDiag.Hardware.Tests;

/// <summary>Факт, который ходит в живой TPU. По умолчанию пропускается (офлайн, воспроизводимый
/// `dotnet test`); для ручного прогона: <c>SZDIAG_LIVE=1 dotnet test</c>.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SZDIAG_LIVE") != "1")
            Skip = "live-тест: требует сети к TPU. Запуск: SZDIAG_LIVE=1 dotnet test";
    }
}
