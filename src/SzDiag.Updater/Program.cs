using Microsoft.Extensions.Configuration;
using SzDiag.Contracts;
using SzDiag.Contracts.Signpost;
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

        // Один exe на клиента: конфиг вшит при сборке; агенту кладём appsettings.json рядом.
        var embedded = EmbeddedConfig.Read();
        var unpacked = EmbeddedConfig.WriteIfMissing(baseDir, embedded);
        if (unpacked is not null) Console.WriteLine(unpacked);
        var config = EmbeddedConfig.Build(baseDir, embedded);
        var opts = new UpdaterOptions();
        config.Bind(opts);

        var agentExe = Path.Combine(baseDir, "SzDiag.Agent.exe");
        var localVersionPath = Path.Combine(baseDir, "version.txt");

        // 1. Найти hub: конфиг (если отвечает) → LAN-broadcast → адрес из указателя. Не нашёлся —
        // подписанный релиз из указателя (specs/002): агент потом найдёт hub сам.
        using var locatorHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var hubLocator = new HubLocator(
            new LocatorOptions(opts.HubUrl, opts.SignpostUrl, opts.ResolveSignKeys(baseDir),
                LocatorOptions.DefaultModes),
            locatorHttp, SeenSeqStore.NextToExe(), HubLocator.Broadcast(opts.AgentToken));
        var found = await hubLocator.LocateAsync();
        foreach (var r in hubLocator.LastRejected) Console.WriteLine($"  отвергнут {r}");
        if (found is null)
            return await UpdateFromSignpostAsync(opts, agentExe, localVersionPath);
        var hubUrl = found.Url;
        Console.WriteLine($"Hub: {hubUrl} ({found.Source})");

        var client = new HttpUpdateClient(hubUrl, opts.AgentToken);

        // 2. Версия на хосте. Старый hub без /agent/* → деградация на локальный агент; hub не
        // отвечает вовсе (сеть) → подписанный релиз из указателя.
        string hostVersion;
        try { hostVersion = await client.GetVersionAsync(); }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            Console.WriteLine(UpdateFailure.Describe(ex));
            return await UpdateFromSignpostAsync(opts, agentExe, localVersionPath);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine(UpdateFailure.Describe(ex));
            return LaunchLocalOrFail(agentExe, baseDir, "не удалось узнать версию на hub", args);
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
                    return LaunchLocalOrFail(agentExe, baseDir, "битый пакет", args);
                }
                PackageApplier.Apply(tmpZip, baseDir);
                Console.WriteLine("Пакет применён.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Напр. agent.exe залочен (уже запущен) — не заменяем, идём на локальный агент.
                Console.WriteLine($"Не удалось применить обновление: {ex.Message}");
                return LaunchLocalOrFail(agentExe, baseDir, "ошибка применения пакета", args);
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
        return AgentLauncher.LaunchAndWait(agentExe, baseDir, args);
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

// Hub не найден или не отвечает: релиз из указателя, проверка подписи ключом с диска.
async Task<int> UpdateFromSignpostAsync(UpdaterOptions opts, string agentExe, string localVersionPath)
{
    Console.WriteLine("Hub недоступен — беру подписанный релиз из указателя.");
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    var locator = new HubLocator(
        new LocatorOptions(null, opts.SignpostUrl, opts.ResolveSignKeys(baseDir),
            LocatorOptions.DefaultModes),
        http, SeenSeqStore.NextToExe());
    var release = await locator.LocateReleaseAsync();
    foreach (var r in locator.LastRejected) Console.WriteLine($"  отвергнут {r}");
    if (release is null)
    {
        Console.WriteLine("Подписанного релиза в указателе нет (или указатель недоступен).");
        if (File.Exists(agentExe))
        {
            Console.WriteLine("Запускаю локального агента — он поищет hub сам.");
            return AgentLauncher.LaunchAndWait(agentExe, baseDir, args);
        }
        Console.Error.WriteLine("Hub не найден, обновиться неоткуда, локального агента нет.");
        return 2;
    }
    Console.WriteLine($"Релиз {release.Release.Version} из {release.Source}.");

    var localVersion = File.Exists(localVersionPath) ? File.ReadAllText(localVersionPath).Trim() : null;
    ReleaseResult result;
    try { result = await new SignedReleaseUpdater(http, baseDir).ApplyAsync(release, localVersion); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"Не удалось применить обновление: {ex.Message}");
        return LaunchLocalOrFail(agentExe, baseDir, "ошибка применения пакета", args);
    }
    foreach (var n in result.Notes) Console.WriteLine($"  {n}");
    switch (result.Outcome)
    {
        case ReleaseOutcome.UpToDate: Console.WriteLine("Версия актуальна."); break;
        case ReleaseOutcome.Applied: Console.WriteLine("Пакет применён."); break;
        default: return LaunchLocalOrFail(agentExe, baseDir, "подписанный пакет не получен", args);
    }
    if (!File.Exists(agentExe))
    {
        Console.Error.WriteLine("Агент не найден после апдейта: " + agentExe);
        return 1;
    }
    return AgentLauncher.LaunchAndWait(agentExe, baseDir, args);
}

// Деградация: если локальный агент есть — запустить его, иначе фейл.
static int LaunchLocalOrFail(string agentExe, string baseDir, string reason,
    IReadOnlyList<string> agentArgs)
{
    if (File.Exists(agentExe))
    {
        Console.WriteLine($"Запускаю локального агента ({reason}).");
        return AgentLauncher.LaunchAndWait(agentExe, baseDir, agentArgs);
    }
    Console.Error.WriteLine($"Обновление невозможно ({reason}) и локального агента нет.");
    return 3;
}
