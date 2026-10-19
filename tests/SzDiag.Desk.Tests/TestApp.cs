using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(SzDiag.Desk.Tests.TestApp))]

namespace SzDiag.Desk.Tests;

public static class TestApp
{
    /// <summary>Отрисовка — настоящим Skia, а не заглушкой headless: заглушка шрифтов не умеет
    /// подбирать вес во встроенной коллекции, и Inter Medium/SemiBold роняли каждое окно.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<SzDiag.Desk.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
