using Microsoft.Extensions.Configuration;
using SzDiag.Contracts;
using SzDiag.Updater;

var baseDir = AppContext.BaseDirectory;

var config = new ConfigurationBuilder()
    .SetBasePath(baseDir)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("SZUPDATER_")
    .Build();
var opts = new UpdaterOptions();
config.Bind(opts);

var agentExe = Path.Combine(baseDir, "SzDiag.Agent.exe");
var localVersionPath = Path.Combine(baseDir, "version.txt");

try
{
    // 1. Найти hub (требуем hub — без него агент всё равно бесполезен).
    var hubUrl = !string.IsNullOrWhiteSpace(opts.HubUrl)
        ? opts.HubUrl
        : await HubDiscovery.FindHubAsync(opts.AgentToken);
    Console.WriteLine($"Hub: {hubUrl}");

    var client = new HttpUpdateClient(hubUrl, opts.AgentToken);

    // 2. Версия на хосте. Старый hub без /agent/* → деградация на локальный агент.
    string hostVersion;
    try { hostVersion = await client.GetVersionAsync(); }
    catch (HttpRequestException)
    {
        Console.WriteLine("Hub не поддерживает апдейт (нет /agent/version).");
        return LaunchLocalOrFail(agentExe, baseDir, "hub без апдейт-эндпоинта");
    }

    var localVersion = File.Exists(localVersionPath) ? File.ReadAllText(localVersionPath).Trim() : null;

    // 3. Обновление, если версии разошлись.
    if (localVersion != hostVersion)
    {
        Console.WriteLine($"Обновление: {localVersion ?? "(нет)"} -> {hostVersion}");
        var tmpZip = Path.Combine(Path.GetTempPath(), $"szpkg-{Guid.NewGuid():N}.zip");
        try
        {
            await client.DownloadPackageAsync(tmpZip);
            var expected = await client.GetPackageSha256Async();
            var actual = Hashing.Sha256File(tmpZip);
            if (actual != expected)
            {
                Console.WriteLine($"sha256 не сошёлся (ожидали {expected}, получили {actual}).");
                return LaunchLocalOrFail(agentExe, baseDir, "битый пакет");
            }
            PackageApplier.Apply(tmpZip, baseDir);
            Console.WriteLine("Пакет применён.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Напр. agent.exe залочен (уже запущен) — не заменяем, идём на локальный агент.
            Console.WriteLine($"Не удалось применить обновление: {ex.Message}");
            return LaunchLocalOrFail(agentExe, baseDir, "ошибка применения пакета");
        }
        finally { try { File.Delete(tmpZip); } catch { } }
    }
    else
    {
        Console.WriteLine("Версия актуальна.");
    }

    // 4. Запустить агента.
    if (!File.Exists(agentExe))
    {
        Console.Error.WriteLine("Агент не найден после апдейта: " + agentExe);
        return 1;
    }
    return AgentLauncher.LaunchAndWait(agentExe, baseDir);
}
catch (HubNotFoundException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

// Деградация: если локальный агент есть — запустить его, иначе фейл.
static int LaunchLocalOrFail(string agentExe, string baseDir, string reason)
{
    if (File.Exists(agentExe))
    {
        Console.WriteLine($"Запускаю локального агента ({reason}).");
        return AgentLauncher.LaunchAndWait(agentExe, baseDir);
    }
    Console.Error.WriteLine($"Обновление невозможно ({reason}) и локального агента нет.");
    return 3;
}
