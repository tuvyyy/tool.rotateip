using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetworkProfileManager.Infrastructure.BrowserAdapters;

// A Windows job owns only processes created by this application. KILL_ON_JOB_CLOSE also covers app crashes.
public sealed class BrowserProcessJob : IDisposable
{
    private SafeFileHandle? job;
    private readonly object sync = new();
    public int ProcessId { get; private set; }
    public static BrowserProcessJob Start(ProcessStartInfo info)
    {
        var owner = new BrowserProcessJob();
        try
        {
            owner.job = CreateJobObject(IntPtr.Zero, null);
            if (owner.job.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(owner.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception();
            var command = new StringBuilder(string.Join(" ", new[] { info.FileName }.Concat(info.ArgumentList).Select(QuoteArgument)));
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1,
                ShowWindow = (ushort)(info.WindowStyle switch { ProcessWindowStyle.Hidden => 0, ProcessWindowStyle.Minimized => 7, ProcessWindowStyle.Maximized => 3, _ => 1 }) };
            if (!CreateProcess(info.FileName, command, IntPtr.Zero, IntPtr.Zero, false, 4u | (info.CreateNoWindow ? 0x08000000u : 0u), IntPtr.Zero, info.WorkingDirectory, ref startup, out var process))
                throw new Win32Exception();
            try
            {
                if (!AssignProcessToJobObject(owner.job, process.Process)) { TerminateProcess(process.Process, 1); throw new Win32Exception(); }
                owner.ProcessId = process.ProcessId;
                if (ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception();
            }
            finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }
    public bool IsRunning
    {
        get
        {
            lock (sync)
            {
            if (job is null || job.IsClosed || job.IsInvalid) return false;
            if (!QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero)) return false;
            return accounting.ActiveProcesses > 0;
            }
        }
    }
    public void Dispose() { lock (sync) { job?.Dispose(); job = null; } }
    public async Task CloseGracefullyAsync()
    {
        var requested = false;
        foreach (var pid in GetProcessIds())
        {
            try { using var process = Process.GetProcessById(pid); requested |= process.CloseMainWindow(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (IsRunning && DateTime.UtcNow < deadline)
        {
            if (!requested)
            {
                foreach (var pid in GetProcessIds())
                    try { using var process = Process.GetProcessById(pid); requested |= process.CloseMainWindow(); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
            }
            await Task.Delay(100);
        }
        lock (sync)
        {
            if (job is not null && !job.IsClosed && !TerminateJobObject(job, 0)) throw new Win32Exception();
        }
        // Closing the job handle requests termination asynchronously. Wait for the whole job, not
        // just a snapshot of root PIDs, before allowing a new browser to reuse the same profile.
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (IsRunning && DateTime.UtcNow < deadline) await Task.Delay(30);
        if (IsRunning) throw new TimeoutException("Windows chưa hoàn tất đóng các tiến trình của profile.");
        Dispose();
    }
    public IReadOnlyList<int> GetProcessIds()
    {
        lock (sync)
        {
        if (job is null || job.IsClosed) return [];
        var size = 8 + IntPtr.Size * 4096; var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryProcessIds(job, 3, buffer, (uint)size, IntPtr.Zero)) throw new Win32Exception();
            var count = Marshal.ReadInt32(buffer, 4);
            return Enumerable.Range(0, count).Select(i => (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size)).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    public static string QuoteArgument(string argument)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessUserTimeLimit, JobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    { public long A, B, C, D; public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    { public int Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public ushort ShowWindow, Reserved2Size; public IntPtr Reserved2, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out Accounting info, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "QueryInformationJobObject")] private static extern bool QueryProcessIds(SafeFileHandle job, int type, IntPtr info, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
}
