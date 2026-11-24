using System.Text;
using System.Text.Json;

namespace SzDiag.Contracts.Fleet;

/// <summary>Раскладка секретов флота в папке <c>secrets\</c> (она в .gitignore):
/// <c>fleet-sign.key</c>, <c>fleet-sign.pub</c>, <c>svc_diag_key</c>, <c>svc_diag_key.pub</c>,
/// <c>fleet.json</c> (токены и параметры ZT). Имена ssh-ключа совпадают с прежними —
/// build-dist и CLI находят его там же, где раньше.</summary>
public static class FleetSecretsStore
{
    public const string SignKeyFile = "fleet-sign.key";
    public const string SignPubFile = "fleet-sign.pub";
    public const string SshKeyFile = "svc_diag_key";
    public const string SshPubFile = "svc_diag_key.pub";
    public const string FleetJsonFile = "fleet.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record FleetJson(int FormatVersion, string AgentToken, string ManagementToken,
        string? ZtControllerUrl, string? ZtControllerToken, string? ZtIdentitySecret, string? ZtNetwork,
        DateTimeOffset CreatedAt);

    public static bool Exists(string dir) => File.Exists(Path.Combine(dir, FleetJsonFile));

    public static void Write(string dir, FleetSecrets s)
    {
        Directory.CreateDirectory(dir);
        WriteText(dir, SignKeyFile, s.SignPrivateKey);
        WriteText(dir, SignPubFile, s.SignPublicKey);
        WriteText(dir, SshKeyFile, s.SvcDiagKey);
        WriteText(dir, SshPubFile, s.SvcDiagKeyPub);
        WriteText(dir, FleetJsonFile, JsonSerializer.Serialize(
            new FleetJson(s.FormatVersion, s.AgentToken, s.ManagementToken,
                s.ZtControllerUrl, s.ZtControllerToken, s.ZtIdentitySecret, s.ZtNetwork, s.CreatedAt), Json));
    }

    public static FleetSecrets Read(string dir)
    {
        var meta = JsonSerializer.Deserialize<FleetJson>(ReadText(dir, FleetJsonFile), Json)
                   ?? throw new InvalidDataException($"{FleetJsonFile} пуст");
        return new FleetSecrets
        {
            FormatVersion = meta.FormatVersion,
            SignPrivateKey = ReadText(dir, SignKeyFile),
            SignPublicKey = ReadText(dir, SignPubFile),
            SvcDiagKey = ReadText(dir, SshKeyFile),
            SvcDiagKeyPub = ReadText(dir, SshPubFile),
            AgentToken = meta.AgentToken,
            ManagementToken = meta.ManagementToken,
            ZtControllerUrl = meta.ZtControllerUrl ?? "",
            ZtControllerToken = meta.ZtControllerToken ?? "",
            ZtIdentitySecret = meta.ZtIdentitySecret ?? "",
            ZtNetwork = meta.ZtNetwork ?? "",
            CreatedAt = meta.CreatedAt,
        };
    }

    private static string ReadText(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) throw new FileNotFoundException($"нет {path}", path);
        return File.ReadAllText(path);
    }

    // Без BOM и с LF: ssh-keygen/OpenSSH не читает приватный ключ с BOM, а PEM-парсер — с ним.
    private static void WriteText(string dir, string name, string content)
        => File.WriteAllText(Path.Combine(dir, name), content.Replace("\r\n", "\n"), new UTF8Encoding(false));
}
