using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli alive <СЗ>` — heartbeat, boot-time, TCP-пробы, ICMP и ARP одной командой
/// (бэклог п.202, СЗ 161972): раньше вердикт «вырубилась или висит» давали шесть ручных
/// прогонов `szcli list`, `Test-NetConnection`, `Test-Connection`, `arp -a`, и решающим
/// оказывалось знание «нет ARP-записи = питания нет», жившее только в голове инженера.</summary>
public static class AliveCommand
{
    /// <summary>Порты, которые агент/sshd/SMB обычно держат открытыми на клиентской Windows —
    /// достаточно одного ответившего, чтобы сказать «сеть жива».</summary>
    private static readonly int[] ProbePorts = { 22, 445, 135, 3389 };

    public static async Task<int> RunAsync(IHubApiClient client, string sz)
    {
        var sessions = await client.GetSessionsAsync();
        var session = sessions.FirstOrDefault(s => s.Sz == sz);

        var target = await client.GetTargetAsync(sz);
        if (target is null && session is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] — hub не знает такого IP ни из активных, ни из истории.");
            return 1;
        }

        var heartbeatAge = session is not null ? DateTimeOffset.UtcNow - session.LastHeartbeat : (TimeSpan?)null;
        if (session is not null)
        {
            var ago = heartbeatAge!.Value.TotalSeconds < 1 ? "только что" : $"{FormatSpan(heartbeatAge.Value)} назад";
            AnsiConsole.MarkupLineInterpolated($"[grey]Heartbeat:[/] {ago} · статус hub: {session.Status}");
        }
        else
            AnsiConsole.MarkupLine("[grey]Heartbeat:[/] СЗ нет в активных на hub (офлайн или уже закрыта)");
        if (session?.BootTime is { } boot)
            AnsiConsole.MarkupLineInterpolated($"[grey]Boot-time:[/] {boot.ToLocalTime():dd.MM HH:mm:ss}");

        var ip = target?.Ip ?? session?.Ip;
        if (ip is null)
        {
            AnsiConsole.MarkupLine("[yellow]IP неизвестен — сетевые пробы пропущены.[/]");
            return 0;
        }

        var icmpOk = await PingAsync(ip);
        AnsiConsole.MarkupLineInterpolated($"[grey]ICMP:[/] {(icmpOk ? "[green]отвечает[/]" : "[dim]молчит[/]")}");

        var openPorts = new List<int>();
        foreach (var port in ProbePorts)
            if (await TcpProbeAsync(ip, port)) openPorts.Add(port);
        if (openPorts.Count > 0)
            AnsiConsole.MarkupLineInterpolated($"[grey]TCP:[/] [green]открыт(ы) {string.Join(", ", openPorts)}[/]");
        else
            AnsiConsole.MarkupLine("[grey]TCP:[/] [dim]ни один из проверенных портов не отвечает[/]");

        var arpFound = ArpTableParser.HasEntry(await RunArpAsync(), ip);
        AnsiConsole.MarkupLineInterpolated($"[grey]ARP:[/] {(arpFound ? "[green]запись есть[/]" : "[dim]записи нет[/]")}");

        var verdict = AliveVerdict.Describe(new AliveSignals(heartbeatAge, arpFound, icmpOk, openPorts.Count > 0));
        AnsiConsole.MarkupLineInterpolated($"\n[bold]Вердикт:[/] {Markup.Escape(verdict)}");
        return 0;
    }

    private static string FormatSpan(TimeSpan t) => t.TotalMinutes < 1
        ? $"{t.TotalSeconds:0} с" : t.TotalHours < 1
        ? $"{t.TotalMinutes:0} мин" : $"{t.TotalHours:0.#} ч";

    private static async Task<bool> PingAsync(string ip)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 1500);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private static async Task<bool> TcpProbeAsync(string ip, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(ip, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(1.5)));
            return completed == connectTask && client.Connected;
        }
        catch { return false; }
    }

    /// <summary>`arp -a <ip>` — живая ARP-таблица хоста уже знает нужную запись, если машина
    /// в той же сети отвечала на канальном уровне недавно.</summary>
    private static async Task<string> RunArpAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("arp", "-a")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output;
        }
        catch { return ""; }
    }
}
