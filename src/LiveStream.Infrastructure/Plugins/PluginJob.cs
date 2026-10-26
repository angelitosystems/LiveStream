using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveStream.Infrastructure.Plugins;

/// <summary>
/// Objeto de trabajo de Windows que agrupa los procesos de plugins: si LiveStream termina (incluso por un cierre
/// inesperado), Windows mata a todos sus plugins, así que no quedan procesos huérfanos. También limita la memoria
/// de cada plugin.
/// </summary>
internal static partial class PluginJob
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private const uint ProcessMemory = 0x100;

    private static readonly Lazy<nint> Handle = new(Create);

    public static long MemoryLimitBytes { get; set; } = 1024L * 1024 * 1024;

    public static void Assign(Process process)
    {
        if (!AssignProcessToJobObject(Handle.Value, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo aislar el proceso del plugin.");
    }

    private static nint Create()
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose | ProcessMemory },
            ProcessMemoryLimit = (nuint)MemoryLimitBytes,
        };
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref info, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // El identificador no se cierra nunca: muere con el proceso de LiveStream y entonces Windows cierra el trabajo.
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);
}
