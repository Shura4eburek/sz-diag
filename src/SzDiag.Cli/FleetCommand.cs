using System.Security.Cryptography;
using System.Text;
using SzDiag.Contracts.Fleet;
using SzDiag.Contracts.Signing;

namespace SzDiag.Cli;

/// <summary>`szcli fleet keygen|encrypt|decrypt` — ключи флота (specs/002-domain-independent-access).
/// Зовётся из <c>tools\fleet.ps1</c>; hub для этих команд не нужен. Пароль флота — из
/// <c>SZDIAG_FLEET_PASSWORD</c> или запросом без эха; на диск он не пишется никогда.</summary>
public static class FleetCommand
{
    public const int Ok = 0;
    public const int Usage = 1;
    public const int AlreadyExists = 2;
    public const int WrongPassword = 4;
    public const int BadBundle = 5;

    public const string PasswordEnv = "SZDIAG_FLEET_PASSWORD";
    public const string BundleEnv = "SZDIAG_FLEET_BUNDLE";

    public static int Run(string[] args, TextWriter? output = null, TextWriter? errors = null,
        Func<string, string?>? readPassword = null)
    {
        output ??= Console.Out;
        errors ??= Console.Error;
        readPassword ??= ReadPasswordInteractive;

        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var secretsDir = Option(args, "--secrets") ?? "secrets";

        try
        {
            return sub switch
            {
                "keygen" => Keygen(args, secretsDir, output, errors),
                "encrypt" => Encrypt(secretsDir, output, errors, readPassword),
                "decrypt" => Decrypt(args, secretsDir, output, errors, readPassword),
                "sign-release" => SignRelease(args, secretsDir, output, errors),
                "set-zt" => SetZt(args, secretsDir, output, errors),
                _ => PrintUsage(errors),
            };
        }
        catch (FileNotFoundException ex)
        {
            errors.WriteLine($"fleet: {ex.Message}");
            return Usage;
        }
    }

    /// <summary>Собрать секреты флота в папке. ssh-ключ уже должен лежать там (его делает
    /// ssh-keygen из fleet.ps1 — либо это прежний ключ этой машины, который так и остаётся).</summary>
    private static int Keygen(string[] args, string dir, TextWriter output, TextWriter errors)
    {
        var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
        if (FleetSecretsStore.Exists(dir) && !force)
        {
            errors.WriteLine($"fleet: в {dir} уже есть {FleetSecretsStore.FleetJsonFile} — перезапись только с --force");
            return AlreadyExists;
        }

        var sshKey = Path.Combine(dir, FleetSecretsStore.SshKeyFile);
        var sshPub = Path.Combine(dir, FleetSecretsStore.SshPubFile);
        if (!File.Exists(sshKey) || !File.Exists(sshPub))
        {
            errors.WriteLine($"fleet: нет {sshKey}(.pub) — сгенерируй ssh-keygen'ом (это делает fleet.ps1 init)");
            return Usage;
        }

        var (priv, pub) = FleetKeys.Generate();
        var secrets = new FleetSecrets
        {
            SignPrivateKey = priv,
            SignPublicKey = pub,
            // Прежний токен этой машины можно сделать токеном флота: тогда уже разданные с неё
            // апдейтеры не требуют переходного списка.
            AgentToken = NonEmpty(Option(args, "--agent-token")) ?? NewToken(),
            ManagementToken = NonEmpty(Option(args, "--management-token")) ?? NewToken(),
            SvcDiagKey = File.ReadAllText(sshKey),
            SvcDiagKeyPub = File.ReadAllText(sshPub),
        };
        var zt = Option(args, "--zt-from");
        if (zt is not null) secrets = ApplyZt(secrets, ZtSettings.Load(zt));
        FleetSecretsStore.Write(dir, secrets);
        output.WriteLine($"fleet: секреты флота созданы в {dir} (kid {KidOf(pub)})");
        return Ok;
    }

    /// <summary>Параметры сервисной ZT-сети, которые пишет <c>tools\zt-network.ps1</c> в
    /// <c>secrets\zt.json</c>: адрес и токен прокси контроллера, бэкап его identity, id сети.</summary>
    public sealed record ZtSettings(string? ControllerUrl, string? ControllerToken, string? IdentitySecret, string? Network)
    {
        public static ZtSettings Load(string path) =>
            System.Text.Json.JsonSerializer.Deserialize<ZtSettings>(File.ReadAllText(path),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException($"{path} пуст");
    }

    // Пустое поле в zt.json не затирает уже лежащее во флоте — правится только то, что передано.
    private static FleetSecrets ApplyZt(FleetSecrets s, ZtSettings z) => s with
    {
        ZtControllerUrl = NonEmpty(z.ControllerUrl?.TrimEnd('/')) ?? s.ZtControllerUrl,
        ZtControllerToken = NonEmpty(z.ControllerToken) ?? s.ZtControllerToken,
        ZtIdentitySecret = NonEmpty(z.IdentitySecret?.Trim()) ?? s.ZtIdentitySecret,
        ZtNetwork = NonEmpty(z.Network) ?? s.ZtNetwork,
    };

    /// <summary>`fleet set-zt --from secrets\zt.json` — дописать параметры ZT-сети в fleet.json.
    /// Остальные секреты не трогаются; в Railway уезжает следующим <c>fleet.ps1 push</c>.</summary>
    private static int SetZt(string[] args, string dir, TextWriter output, TextWriter errors)
    {
        var from = Option(args, "--from");
        if (from is null)
        {
            errors.WriteLine("fleet: set-zt --from <zt.json>");
            return Usage;
        }
        var updated = ApplyZt(FleetSecretsStore.Read(dir), ZtSettings.Load(from));
        if (updated.ZtNetwork.Length is not (0 or 16))
        {
            errors.WriteLine($"fleet: id сети должен быть 16 hex, получено «{updated.ZtNetwork}»");
            return Usage;
        }
        FleetSecretsStore.Write(dir, updated);
        output.WriteLine($"fleet: ZT записан во флот (сеть {updated.ZtNetwork}, контроллер {updated.ZtControllerUrl})");
        return Ok;
    }

    private static int Encrypt(string dir, TextWriter output, TextWriter errors, Func<string, string?> readPassword)
    {
        var secrets = FleetSecretsStore.Read(dir);
        var password = Environment.GetEnvironmentVariable(PasswordEnv);
        if (string.IsNullOrEmpty(password))
        {
            password = readPassword("Пароль флота: ");
            var again = readPassword("Повтори пароль флота: ");
            if (password != again)
            {
                errors.WriteLine("fleet: пароли не совпали");
                return Usage;
            }
        }
        if (string.IsNullOrEmpty(password) || password.Length < 12)
        {
            errors.WriteLine("fleet: пароль флота — минимум 12 символов (он единственный барьер, если утечёт аккаунт Railway)");
            return Usage;
        }
        output.Write(FleetBundle.Encrypt(secrets, password));
        return Ok;
    }

    private static int Decrypt(string[] args, string dir, TextWriter output, TextWriter errors, Func<string, string?> readPassword)
    {
        var inFile = Option(args, "--in");
        var bundle = inFile is not null ? File.ReadAllText(inFile) : Environment.GetEnvironmentVariable(BundleEnv);
        if (string.IsNullOrWhiteSpace(bundle))
        {
            errors.WriteLine($"fleet: бандл не передан (--in <файл> или {BundleEnv})");
            return Usage;
        }

        var password = Environment.GetEnvironmentVariable(PasswordEnv);
        if (string.IsNullOrEmpty(password)) password = readPassword("Пароль флота: ");

        FleetSecrets secrets;
        try { secrets = FleetBundle.Decrypt(bundle, password ?? ""); }
        catch (FleetBundleException ex)
        {
            errors.WriteLine($"fleet: {ex.Message}");
            return ex.Error == FleetBundleError.WrongPassword ? WrongPassword : BadBundle;
        }

        FleetSecretsStore.Write(dir, secrets);
        output.WriteLine($"fleet: секреты флота разложены в {dir} (kid {KidOf(secrets.SignPublicKey)})");
        return Ok;
    }

    /// <summary>Подписать релиз агента (build-dist -Publish): sha256 и размер считаются по самому
    /// файлу пакета, seq — unix-мс. На выходе — конверт для PUT /signpost/release.</summary>
    private static int SignRelease(string[] args, string dir, TextWriter output, TextWriter errors)
    {
        var zip = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        var version = Option(args, "--version");
        var buildTime = Option(args, "--build-time");
        if (zip is null || !File.Exists(zip) || string.IsNullOrWhiteSpace(version)
            || !DateTimeOffset.TryParse(buildTime, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var built))
        {
            errors.WriteLine("fleet: sign-release <package.zip> --version <v> --build-time <ISO-8601> --url <адрес> [--url …]");
            return Usage;
        }
        var urls = args.Select((a, i) => (a, i))
            .Where(t => t.a.Equals("--url", StringComparison.OrdinalIgnoreCase) && t.i + 1 < args.Length)
            .Select(t => args[t.i + 1])
            .ToList();
        if (urls.Count == 0)
        {
            errors.WriteLine("fleet: sign-release — нужен хотя бы один --url пакета");
            return Usage;
        }

        string sha;
        using (var fs = File.OpenRead(zip)) sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        var record = new SzDiag.Contracts.Signpost.ReleaseRecord(version.Trim(), built.ToUniversalTime(), sha,
            new FileInfo(zip).Length, urls.Select(u => u.Replace("{sha256}", sha)).ToList(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        using var signer = EnvelopeSigner.FromPrivatePem(
            File.ReadAllText(Path.Combine(dir, FleetSecretsStore.SignKeyFile)));
        output.Write(signer.SignRecord(record).ToJson());
        return Ok;
    }

    private static string KidOf(string publicPem)
    {
        using var key = FleetKeys.ImportPem(publicPem);
        return FleetKeys.KeyId(key);
    }

    private static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Option(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? ReadPasswordInteractive(string prompt)
    {
        if (Console.IsInputRedirected) return null;
        Console.Error.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    private static int PrintUsage(TextWriter errors)
    {
        errors.WriteLine("""
            szcli fleet keygen  [--secrets <папка>] [--agent-token T] [--management-token T] [--zt-from <zt.json>] [--force]
            szcli fleet set-zt  [--secrets <папка>] --from <zt.json>   → параметры ZT-сети в fleet.json
            szcli fleet encrypt [--secrets <папка>]            → бандл в stdout
            szcli fleet decrypt [--secrets <папка>] [--in <файл>]  (иначе бандл из SZDIAG_FLEET_BUNDLE)
            szcli fleet sign-release <package.zip> --version <v> --build-time <ISO> --url <адрес> [--url …]
                  ({sha256} в адресе подставится)  → подписанный конверт релиза в stdout
            Пароль флота — SZDIAG_FLEET_PASSWORD или запросом. Обычно зовётся из tools\fleet.ps1.
            """);
        return Usage;
    }
}
