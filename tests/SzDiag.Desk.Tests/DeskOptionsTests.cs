using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

/// <summary>19.10: Desk из исходников стучался в dev-порт 5000 с dev-токеном, не видел hub из dist
/// и запускал второй. Теперь по умолчанию — порт hub, а свой адрес/токен — в appsettings.local.json.</summary>
public sealed class DeskOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szdesk-opts-" + Guid.NewGuid().ToString("N"));

    public DeskOptionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void NoConfig_DefaultsToHubPort()
        => Assert.Equal("http://localhost:5099", DeskOptions.Load(_dir).HubBaseUrl);

    [Fact]
    public void LocalConfig_OverridesBase()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"),
            """{ "HubBaseUrl": "http://localhost:5099", "ManagementToken": "dev-token", "KbRoot": "kb" }""");
        File.WriteAllText(Path.Combine(_dir, "appsettings.local.json"),
            """{ "HubBaseUrl": "http://localhost:6100", "ManagementToken": "real" }""");

        var o = DeskOptions.Load(_dir);

        Assert.Equal("http://localhost:6100", o.HubBaseUrl);
        Assert.Equal("real", o.ManagementToken);
        Assert.Equal("kb", o.KbRoot);   // чего нет в local — берётся из базового
    }
}
