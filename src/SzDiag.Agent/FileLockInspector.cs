using System.Runtime.InteropServices;

namespace SzDiag.Agent;

/// <summary>Кто держит файл открытым — через Restart Manager (тот же API, что использует
/// диалог «Файл занят» в проводнике). `FileShare.ReadWrite | FileShare.Delete` не спасает,
/// если писатель открыл файл вовсе без совместного чтения (лог живого <c>sshd</c>) — тогда
/// <c>pull</c> падает на sharing violation, и «занято» без имени процесса не помогает
/// разобраться, стоит ли ждать (бэклог п.104): пользовательский код не может прочитать байты
/// такого файла без VSS/RestartManager, но хотя бы сказать, КТО держит, можно.</summary>
public static class FileLockInspector
{
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const int CchRmSessionKeyLen = 32;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, System.Text.StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    /// <summary>Список "имя (pid)" процессов, держащих файл открытым. Пустой список — не
    /// смогли определить (нет прав, API недоступно) или файл никем не занят.</summary>
    public static IReadOnlyList<string> WhoIsLocking(string path)
    {
        var result = new List<string>();
        uint handle = 0;
        try
        {
            // strSessionKey — ВЫХОДНОЙ буфер: RmStartSession сам пишет в него ключ из
            // CCH_RM_SESSION_KEY_LEN (32) символов + нуль. Неизменяемая .NET-строка короче
            // 33 символов здесь = запись за границу буфера и порча кучи (тест-хост падал
            // с Fatal Internal CLR error 0x80131506 в соседних тестах).
            var key = new System.Text.StringBuilder(CchRmSessionKeyLen + 1);
            if (RmStartSession(out handle, 0, key) != 0) return result;

            var files = new[] { path };
            if (RmRegisterResources(handle, (uint)files.Length, files, 0, null, 0, null) != 0)
                return result;

            uint needed = 0, count = 0, reasons = 0;
            var rc = RmGetList(handle, out needed, ref count, null, ref reasons);
            if (rc != 0 && rc != ErrorMoreData) return result;
            if (needed == 0) return result;

            count = needed;
            var infos = new RM_PROCESS_INFO[count];
            rc = RmGetList(handle, out needed, ref count, infos, ref reasons);
            if (rc != 0) return result;

            for (var i = 0; i < count; i++)
                result.Add($"{infos[i].strAppName} ({infos[i].Process.dwProcessId})");
        }
        catch
        {
            // rstrtmgr.dll недоступна (не Windows) или нет прав - молча возвращаем пустой список,
            // вызывающий код падает обратно на исходное сообщение об ошибке.
        }
        finally
        {
            if (handle != 0)
                try { RmEndSession(handle); } catch { /* сессия и так закроется с процессом */ }
        }
        return result;
    }
}
