using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(SzDiag.Desk.Tests.TestApp))]

namespace SzDiag.Desk.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<SzDiag.Desk.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
