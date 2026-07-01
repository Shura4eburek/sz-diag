namespace SzDiag.Agent;

/// <summary>Результат захвата экрана: либо PNG-байты, либо причина недоступности.</summary>
public sealed record ScreenCapture(byte[]? Png, string? Error);

public interface IScreenCapturer
{
    ScreenCapture Capture();
}
