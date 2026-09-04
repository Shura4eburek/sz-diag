using Spectre.Console;

namespace SzDiag.Cli;

/// <summary>`szcli close` кричит про забытый откат настроек сетевого адаптера — как уже
/// кричит про забытый `unfreeze` (бэклог п.206, СЗ 162367).
///
/// Рецепты, трогающие энергосбережение адаптера (`net-power-off.ps1`), пишут бэкап прежних
/// значений в <see cref="BackupPath"/> ДО первой правки — сама правка ресетит адаптер и может
/// оборвать exec-канал на полуслове. Если файл остался на клиенте, откат не был сделан
/// (`$Restore = $true` того же рецепта), и машина уедет с изменённым сетевым адаптером без
/// следа в kb.</summary>
public static class NetAdapterBackupCheck
{
    /// <summary>Путь на клиенте — тот же, что использует `net-power-off.ps1`.</summary>
    public const string BackupPath = @"C:\ProgramData\szdiag\net-adv-backup.json";

    /// <summary>Скрипт разведки: печатает `True`/`False` — есть ли незакрытый бэкап.</summary>
    public static string ProbeScript => $"Test-Path '{BackupPath}'";

    /// <summary>Разбор ответа разведки. Чистая функция — тестируется без сети.</summary>
    public static bool ParseExists(string? stdout)
        => stdout is not null && stdout.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);

    /// <summary>Проверяет клиента (пока он ещё онлайн — после close канала не будет) и печатает
    /// громкое предупреждение, если откат сетевых настроек забыт. Не блокирует close: как и
    /// заморозка WU, это громкий совет, а не отказ.</summary>
    public static async Task WarnIfLeftoverAsync(IHubApiClient client, string sz)
    {
        var res = await client.ExecAsync(sz, ProbeScript, 20);
        if (res is null || !ParseExists(res.StdOut)) return;

        AnsiConsole.MarkupLine(
            $"[red]⚠ На СЗ {sz} остался незакрытый бэкап настроек сетевого адаптера![/] "
            + $"({BackupPath}) Откати до отдачи машины: szcli exec {sz} -f tools\\recipes\\client\\net-power-off.ps1 --param Restore=true --detach");
    }
}
