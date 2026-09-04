using SzDiag.Contracts;

namespace SzDiag.Updater;

/// <summary>Точка входа на клиенте не должна жить в синхронизируемой облачной папке
/// (OneDrive/Dropbox/…): на СЗ 160636 агент оказался в
/// <c>C:\Users\User\OneDrive\Робочий стіл\Client-test</c>, и <c>state.json</c> + логи текущей
/// сессии стали синхронизироваться наружу в личное облако клиента (бэклог п.41) — то, что
/// временный доступ должен откатывать без следов, тут вообще не под нашим контролем.
///
/// <see cref="SzDiag.Agent.ToolsDirectory"/> уже уводит из облака доставленные инструменты
/// (п.63): для самого агента/апдейтера решение проще — не молчать и не запускаться, а не
/// тихо переносить бинарники и переживать за то, что перенос сорвётся на середине.</summary>
public static class CloudInstallGuard
{
    /// <summary>null — путь безопасен. Иначе — готовое сообщение оператору с причиной отказа.</summary>
    public static string? Check(string baseDir)
    {
        if (!CloudSyncPaths.IsSynced(baseDir)) return null;
        return $"Апдейтер запущен из синхронизируемой облачной папки: {baseDir}\n" +
               "Логи и state.json текущей сессии уедут в личное облако клиента и не откатятся " +
               "при закрытии СЗ (бэклог п.41). Перенеси SzDiag.Updater.exe + appsettings.json " +
               "в локальную папку (например C:\\szdiag или C:\\Client-test вне облака) и запусти оттуда.";
    }
}
