using System.Diagnostics;
using System.Security.Principal;

namespace SzDiag.Agent;

/// <summary>Кто и в какой сессии Windows работает агент — уезжает на hub при регистрации.
///
/// Боль (бэклог п.220, СЗ 123123, 2026-08-27): до ребута `szcli exec` шёл под
/// `desktop-...\kiril`, session 1 — скриншот снимался, `Start-Process` работал. После ребута
/// агент поднялся автостарт-задачей под `NT AUTHORITY\СИСТЕМА` — и то же самое сломалось
/// БЕЗ ВНЯТНОЙ ОШИБКИ: `setup.exe /auto upgrade` стартовал и мгновенно исчезал, снимок экрана
/// падал `Win32Exception` в `CopyFromScreen`. Получаса ушло на версии «UAC/Secure Desktop» и
/// «setup упал по совместимости», пока `whoami` в exec не показал СИСТЕМА. Печатать это
/// заранее — не гадать заново на каждой заявке.</summary>
public static class AgentIdentity
{
    /// <summary>«NT AUTHORITY\СИСТЕМА» после автостарт-задачи или «машина\юзер» при ручном
    /// запуске под интерактивным пользователем.</summary>
    public static string CurrentUser()
    {
        try { return WindowsIdentity.GetCurrent().Name; }
        catch { return "?"; }
    }

    /// <summary>Сессия Windows текущего процесса. 0 — служебная, без рабочего стола: GUI-операции
    /// оттуда ломаются молча (см. класс).</summary>
    public static int CurrentSessionId()
    {
        try { return Process.GetCurrentProcess().SessionId; }
        catch { return -1; }
    }
}
