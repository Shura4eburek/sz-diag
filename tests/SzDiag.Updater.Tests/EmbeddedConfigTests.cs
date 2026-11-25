using Microsoft.Extensions.Configuration;
using SzDiag.Updater;

namespace SzDiag.Updater.Tests;

/// <summary>Один exe на клиента: конфиг вшит в апдейтер, агенту раскладывается рядом, правки на
/// месте главнее вшитого.</summary>
public class EmbeddedConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szupd-emb-" + Guid.NewGuid().ToString("N"));
    private const string Embedded = """{"SignpostUrl":"https://sp.example","AgentToken":"tok","HubUrl":""}""";

    public EmbeddedConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Missing_appsettings_is_written_from_embedded_config()
    {
        var msg = EmbeddedConfig.WriteIfMissing(_dir, Embedded);

        Assert.NotNull(msg);
        Assert.Equal(Embedded, File.ReadAllText(Path.Combine(_dir, "appsettings.json")));
    }

    [Fact]
    public void Existing_appsettings_is_never_overwritten()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), """{"AgentToken":"local"}""");

        Assert.Null(EmbeddedConfig.WriteIfMissing(_dir, Embedded));
        Assert.Equal("""{"AgentToken":"local"}""", File.ReadAllText(Path.Combine(_dir, "appsettings.json")));
    }

    [Fact]
    public void Without_embedded_config_nothing_is_written()
    {
        Assert.Null(EmbeddedConfig.WriteIfMissing(_dir, null));
        Assert.False(File.Exists(Path.Combine(_dir, "appsettings.json")));
    }

    [Fact]
    public void Local_file_overrides_embedded_values_and_embedded_fills_the_rest()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), """{"AgentToken":"local"}""");
        var opts = new UpdaterOptions();
        EmbeddedConfig.Build(_dir, Embedded).Bind(opts);

        Assert.Equal("local", opts.AgentToken);
        Assert.Equal("https://sp.example", opts.SignpostUrl);
    }

    [Fact]
    public void Only_embedded_config_is_enough()
    {
        var opts = new UpdaterOptions();
        EmbeddedConfig.Build(_dir, Embedded).Bind(opts);

        Assert.Equal("tok", opts.AgentToken);
        Assert.Equal("https://sp.example", opts.SignpostUrl);
    }
}
