using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace SzDiag.Hub.Tests;

/// <summary>Общие настройки тестового хоста hub.</summary>
public static class HubTestHost
{
    /// <summary>Снимает провайдеры логирования. По умолчанию на Windows подключается EventLog,
    /// и при остановке тестового хоста он дописывает в уже освобождённый объект —
    /// `dotnet test` получал 4–7 «падений» на class cleanup
    /// (`Cannot access a disposed object. Object name: 'EventLogInternal'`), причём набор
    /// «упавших» тестов менялся от прогона к прогону, так что зелёный прогон нельзя было
    /// отличить от сломанного (бэклог п.65). Журнал Windows в тестах не нужен.</summary>
    public static IWebHostBuilder WithoutSystemLogging(this IWebHostBuilder builder)
        => builder.ConfigureLogging(logging => logging.ClearProviders());
}
