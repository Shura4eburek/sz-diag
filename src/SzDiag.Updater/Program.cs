using Microsoft.Extensions.Configuration;
using SzDiag.Contracts;
using SzDiag.Updater;

var baseDir = AppContext.BaseDirectory;

// Лог — раньше всего остального. Апдейтер запускают двойным кликом, и любой ранний выход
// закрывает окно вместе с причиной: на 161642 «окно появилось и сразу пропало», а следа
// не осталось нигде. Причина остаётся в файле рядом с exe; если туда писать нельзя
// (нет прав, облачная папка) — в %TEMP%\szdiag\, но НЕ нигде.
using var log = UpdaterLog.Open(UpdaterLog.PathFor(baseDir));
var rawOut = Console.Out;
Console.SetOut(UpdaterLog.Tee(rawOut, log.Writer));
Console.SetError(UpdaterLog.Tee(Console.Error, log.Writer));
Console.WriteLine($"Папка: {baseDir}");
Console.WriteLine($"Лог: {log.Path ?? "(не удалось открыть — только это окно)"}");

var exitCode = await RunAsync();

Console.WriteLine($"Код возврата: {exitCode}");
// Строка про лог — только в консоль: в самом логе она бессмысленна.
if (exitCode != 0 && log.Path is not null) rawOut.WriteLine($"Подробности в логе: {log.Path}");
log.Writer.Flush();

// Двойной клик из проводника + ошибка = окно закроется вместе с причиной. Именно так на
// 161642 сутки пряталось сообщение про OneDrive: апдейтер его печатал, прочитать было нельзя.
if (ConsolePause.ShouldWait(exitCode))
{
    rawOut.WriteLine();
    rawOut.WriteLine("Нажми Enter, чтобы закрыть окно.");
    try { Console.ReadLine(); } catch { }
}

return exitCode;

async Task<int> RunAsync()
{
    try
    {
        // Точка входа на клиенте не должна жить в облаке (OneDrive/Dropbox/…) — иначе state.json
        // и логи сессии синхронизируются наружу мимо нашего контроля (СЗ 160636, бэклог п.41).
        // Отказываем до всего остального: смысла качать пакет и поднимать агента туда нет.
        var cloudWarning = CloudInstallGuard.Check(baseDir);
        if (cloudWarning is not null)
        {
            Console.Error.WriteLine(cloudWarning);
            return 4;
        }

        var config = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("SZUPDATER_")
            .Build();
        var opts = new UpdaterOptions();
        config.Bind(opts);

        var agentExe = Path.Combine(baseDir, "SzDiag.Agent.exe");
        var localVersionPath = Path.Combine(baseDir, "version.txt");

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
    catch (Exception ex)
    {
        // Любое необработанное исключение раньше уходило вместе с закрытым окном. Стек нужен
        // целиком: разбирать придётся по логу с клиентской машины, второго шанса не будет.
        Console.Error.WriteLine($"Апдейтер упал: {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine(ex.ToString());
        return 5;
    }
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
