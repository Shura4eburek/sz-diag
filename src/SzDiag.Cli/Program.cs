using Microsoft.Extensions.Configuration;
using SzDiag.Cli;
using SzDiag.Kb;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("SZDIAG_")
    .Build();

var options = new CliOptions();
config.Bind(options);

using var http = new HttpClient { BaseAddress = new Uri(options.HubBaseUrl) };
var client = new HubApiClient(http, options.ManagementToken);

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "watch";

switch (command)
{
    case "list":
        Console.WriteLine(SessionTableRenderer.Render(await client.GetSessionsAsync()));
        break;

    case "watch":
        await WatchAsync(client);
        break;

    case "close" when args.Length >= 2:
        Console.WriteLine(await client.CloseAsync(args[1])
            ? $"СЗ {args[1]} закрыта (revert отправлен агенту)."
            : $"СЗ {args[1]} не найдена среди активных.");
        break;

    case "target" when args.Length >= 2:
        var t = await client.GetTargetAsync(args[1]);
        Console.WriteLine(t is null ? $"СЗ {args[1]} не найдена." : t.Ssh);
        break;

    case "kb" when args.Length >= 2:
        await KbCommand.RunAsync(args[1..], options.KbRoot);
        break;

    default:
        Console.WriteLine("""
            Использование:
              szcli [watch]          живой список онлайн-СЗ (по умолчанию)
              szcli list             однократный список
              szcli close <СЗ>       закрыть СЗ (revert на агенте)
              szcli target <СЗ>      SSH-адрес по номеру СЗ
            """);
        break;
}

static async Task WatchAsync(IHubApiClient client)
{
    Console.WriteLine("Живой список онлайн-СЗ. Ctrl+C для выхода.\n");
    while (true)
    {
        IReadOnlyList<SzDiag.Contracts.SessionInfo> sessions;
        try
        {
            sessions = await client.GetSessionsAsync();
        }
        catch (HttpRequestException)
        {
            Console.Clear();
            Console.WriteLine("  hub недоступен, переподключение…");
            await Task.Delay(2000);
            continue;
        }

        Console.Clear();
        Console.WriteLine($"  sz-diag — онлайн-СЗ   {DateTime.Now:HH:mm:ss}\n");
        Console.WriteLine(SessionTableRenderer.Render(sessions));
        await Task.Delay(1000);
    }
}
