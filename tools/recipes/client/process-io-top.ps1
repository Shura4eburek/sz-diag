$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Кто грызёт диск ПО ПРОЦЕССАМ — без perf-счётчиков вообще.
#
# Грабля (СЗ 161972, 21.08.2026, бэклог п.201): в момент, когда линейное чтение упало до
# 22 МБ/с, первый вопрос — «кто ещё нагружает диск». Ни один штатный способ не сработал:
# `Get-Counter '\PhysicalDisk(_Total)\% Idle Time'` не резолвится (имена счётчиков
# локализованы, английский путь мимо на укр/рус Windows), `Get-CimInstance
# Win32_PerfRawData_PerfProc_Process` и `Win32_PerfFormattedData_PerfDisk_PhysicalDisk` дают
# **`Invalid class`** (HRESULT 0x80041010) — счётчики производительности на клиенте
# разрушены (`lodctr /R` + `winmgmt /resyncperf` чинит, но НЕ прямо сейчас, посреди прогона).
# Рецепт при этом отработал «успешно», просто с пустой таблицей — то есть МОЛЧА соврал.
#
# Обход: `GetProcessIoCounters` — прямой Win32 API (kernel32), читает счётчики ядра процесса
# напрямую, НЕ через perflib/lodctr. Работает даже когда весь Performance Counters
# subsystem разрушен.
#
# Отдельная грабля того же захода: PID стресс-задачи ловили через
# `Get-Process | Sort-Object StartTime | Select -First 1` — на 161972 так замерили
# наблюдатель сенсоров вместо реального стресс-скана и получили «0 МБ/с» не про тот процесс.
# `Find-ProcessByTaskName` ниже находит PID по образу задачи (Actions.Execute из XML), а не
# по времени старта — угадать не может в принципе.
#   szcli exec <СЗ> -f tools\recipes\client\process-io-top.ps1
$IntervalSec = 5
$Top         = 20

Add-Type -ErrorAction SilentlyContinue -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SzDiagProcIo {
    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public static IO_COUNTERS Get(int pid) {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) throw new Exception("OpenProcess(" + pid + ") failed, win32=" + Marshal.GetLastWin32Error());
        try {
            IO_COUNTERS c;
            if (!GetProcessIoCounters(h, out c))
                throw new Exception("GetProcessIoCounters(" + pid + ") failed, win32=" + Marshal.GetLastWin32Error());
            return c;
        } finally { CloseHandle(h); }
    }
}
'@

# Явная строка отказа вместо пустой таблицы (п.201, пункт 3): недоступный источник данных
# должен быть виден, а не выглядеть как «нагрузки нет».
function Get-ProcessIoTop([int]$IntervalSeconds, [int]$TopN) {
    $procs = @(Get-Process -ErrorAction SilentlyContinue)
    $before = @{}
    foreach ($p in $procs) {
        try { $before[$p.Id] = [SzDiagProcIo]::Get($p.Id) } catch { }
    }
    if ($before.Count -eq 0) {
        'I/O по процессам НЕДОСТУПЕН: GetProcessIoCounters не сработал ни для одного процесса ' +
        '(нет прав или API заблокирован политикой) — цифры ниже отсутствуют, это не "нагрузки нет".'
        return
    }
    Start-Sleep -Seconds $IntervalSeconds
    $rows = foreach ($p in (Get-Process -ErrorAction SilentlyContinue)) {
        if (-not $before.ContainsKey($p.Id)) { continue }
        try {
            $after = [SzDiagProcIo]::Get($p.Id)
            $b = $before[$p.Id]
            [pscustomobject]@{
                Pid      = $p.Id
                Name     = $p.ProcessName
                ReadMBs  = [math]::Round((($after.ReadTransferCount - $b.ReadTransferCount) / 1MB) / $IntervalSeconds, 1)
                WriteMBs = [math]::Round((($after.WriteTransferCount - $b.WriteTransferCount) / 1MB) / $IntervalSeconds, 1)
            }
        } catch { }
    }
    $rows = @($rows | Where-Object { $_ })
    if ($rows.Count -eq 0) {
        'I/O по процессам НЕДОСТУПЕН: ни один процесс не пережил интервал измерения (все завершились).'
        return
    }
    $rows | Sort-Object { $_.ReadMBs + $_.WriteMBs } -Descending | Select-Object -First $TopN |
        Format-Table -Auto | Out-String -Width 160
}

# Найти PID по образу ЗАДАЧИ (не по времени старта — п.201: "самый свежий процесс" на 161972
# поймал наблюдатель сенсоров вместо стресс-скана). Сверяем Actions.Execute из XML задачи
# с образом реального процесса.
function Find-ProcessByTaskName([string]$TaskName) {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $task) { return $null }
    $exePath = ($task.Actions | Select-Object -First 1).Execute
    if (-not $exePath) { return $null }
    $exeName = [IO.Path]::GetFileNameWithoutExtension($exePath)
    @(Get-Process -Name $exeName -ErrorAction SilentlyContinue)
}

'--- I/O по процессам (GetProcessIoCounters, без perf-счётчиков) ---'
Get-ProcessIoTop -IntervalSeconds $IntervalSec -TopN $Top
