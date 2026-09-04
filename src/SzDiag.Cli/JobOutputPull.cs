using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`exec --result &lt;jobId&gt; --save` — забрать вывод фоновой задачи с клиента на хост
/// тем же каналом, что и `pull`, чтобы он не терялся вместе с машиной (бэклог п.214, СЗ 161972:
/// единственное приборное доказательство дефекта диска пережило только пересказом в журнале,
/// потому что переустановка клиента унесла лог раньше, чем его успели забрать вручную).</summary>
public static class JobOutputPull
{
    /// <summary>Папка задачи на клиенте — тот же путь, что `BackgroundJobs` использует по
    /// умолчанию (<see cref="ClientTraces.JobsRoot"/>).</summary>
    public static string ClientDir(string jobId) => Path.Combine(ClientTraces.JobsRoot, jobId);

    /// <summary>Подпапка на хосте: группируем по jobId, а не по метке времени забора — повторный
    /// `--save` той же задачи (например, пока она ещё выполняется) ложится рядом же.</summary>
    public static string HostLabel(string jobId) => $"jobs/{jobId}";
}
