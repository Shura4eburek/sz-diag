namespace SzDiag.Contracts;

/// <summary>Имена, общие для агента и hub, чтобы не расходились строки.</summary>
public static class HubRoutes
{
    public const string Path = "/agents";

    // Заголовок с pre-shared токеном при коннекте.
    public const string TokenHeader = "X-SzDiag-Token";

    // Методы, которые агент вызывает на hub.
    public const string Register = nameof(Register);
    public const string Heartbeat = nameof(Heartbeat);

    // Метод, который hub вызывает на агенте (client method).
    public const string Revert = nameof(Revert);

    // Hub -> агент: запустить прогон тестов.
    public const string RunTests = nameof(RunTests);

    // Hub -> агент: собрать диагностический снапшот (read-only, по секциям).
    public const string RunDiag = nameof(RunDiag);

    // Hub -> агент: выполнить PowerShell-скрипт и вернуть вывод (замена SSH для сбора данных).
    public const string Exec = nameof(Exec);

    // Агент -> hub: результат Exec (сопоставляется с запросом по RequestId).
    public const string ExecResult = nameof(ExecResult);

    // Hub -> агент: забрать файл(ы) с клиента на хост.
    public const string Pull = nameof(Pull);

    // Агент -> hub: кусок файла и итог забора (сопоставляются по RequestId).
    public const string PullChunk = nameof(PullChunk);
    public const string PullResult = nameof(PullResult);

    // Hub -> агент: скачать инструмент с hub (агент тянет файлы сам, по HTTP).
    public const string Push = nameof(Push);

    // Агент -> hub: итог доставки инструмента.
    public const string PushResult = nameof(PushResult);

    // Апдейтер клиента: раздача пакета агента (HTTP, под TokenHeader).
    public const string AgentApiPrefix = "/agent";
    public const string AgentVersionRoute = "/agent/version";
    public const string AgentPackageRoute = "/agent/package";
    public const string AgentPackageSha256Route = "/agent/package.sha256";

    // Агент -> hub: загрузить файл отчёта.
    public const string UploadReportFile = nameof(UploadReportFile);

    // Агент -> hub: сообщить текущую активность (метка + время старта).
    public const string ReportActivity = nameof(ReportActivity);
}
