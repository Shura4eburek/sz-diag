using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SzDiag.Hub;

/// <summary>
/// Job object с флагом KILL_ON_JOB_CLOSE: назначенные в него процессы система убивает сама,
/// когда умирает владелец job'а. Нужен для cloudflared — штатная остановка через
/// <c>StopAsync</c> не срабатывает, когда hub убивают жёстко (Stop-Process -Force,
/// TerminateProcess), и туннель остаётся висеть, ведя в уже мёртвый hub (502 вместо
/// честного «недоступно»). pid-файл добивает осиротевшего только на следующем старте —
/// job закрывает дыру между смертью hub и этим стартом.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeFileHandle _handle;

    private KillOnCloseJob(SafeFileHandle handle) => _handle = handle;

    /// <summary>Создаёт job. null — система не дала (не Windows, нет прав): вызывающий
    /// продолжает без него, откат тогда держится на pid-файле.</summary>
    public static KillOnCloseJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) return null;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                handle.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new KillOnCloseJob(handle);
    }

    /// <summary>Назначить процесс в job. false — не вышло (процесс уже умер и т.п.).</summary>
    public bool TryAssign(IntPtr processHandle)
    {
        if (_handle.IsInvalid || processHandle == IntPtr.Zero) return false;
        try { return AssignProcessToJobObject(_handle, processHandle); }
        catch { return false; }
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass,
        IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
