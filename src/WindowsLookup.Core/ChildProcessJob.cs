using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsLookup;

/// <summary>在父进程退出时让 Windows 回收语音和选区子进程。</summary>
internal static class ChildProcessJob
{
    private static readonly Lazy<SafeFileHandle?> Job = new(_create);

    // 尽早纳入内核托管，受限宿主中继续依赖管道和父进程退出机制。
    internal static bool _assign(Process process)
    {
        if (Job.Value is not { } job)
        {
            return false;
        }
        try
        {
            if (_assignProcess(job, process.SafeHandle))
            {
                return true;
            }
            Trace.TraceWarning("无法托管 TapGloss 子进程，Windows 错误 {0}。", Marshal.GetLastPInvokeError());
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("TapGloss 子进程无法加入 Job（{0}）。", error.GetType().Name);
        }
        return false;
    }

    // 建立匿名且不可继承的 Job，父进程生命周期内保留句柄。
    private static SafeFileHandle? _create()
    {
        SafeFileHandle job = _createJob(0, null);
        int code = Marshal.GetLastPInvokeError();
        if (!job.IsInvalid)
        {
            const int extendedLimitInformation = 9;
            const uint killOnJobClose = 0x00002000;
            var info = new JobLimits { Basic = new BasicLimits { Flags = killOnJobClose } };
            if (_setJob(job, extendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobLimits>()))
            {
                return job;
            }
            code = Marshal.GetLastPInvokeError();
        }
        job.Dispose();
        Trace.TraceWarning("无法初始化 TapGloss 子进程 Job，Windows 错误 {0}。", code);
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        internal long ProcessUserTime, JobUserTime;
        internal uint Flags;
        internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcesses;
        internal nuint Affinity;
        internal uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobLimits
    {
        internal BasicLimits Basic;
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    // 创建匿名 Job。
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle _createJob(nint attributes, string? name);

    // 设置最后一个句柄关闭时回收进程的扩展限制。
    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _setJob(SafeFileHandle job, int information, ref JobLimits limits, uint size);

    // 将已创建的子进程加入 Job。
    [DllImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool _assignProcess(SafeFileHandle job, SafeProcessHandle process);
}
