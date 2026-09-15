using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XREngine.ControlPlane.Service;

/// <summary>Owns a worker process tree and applies hard CPU/memory bounds. Closing the job kills its members.</summary>
internal sealed class WindowsWorkerJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsWorkerJob"/> class with the specified CPU and memory limits, name, and kill-on-close behavior.
    /// </summary>
    /// <param name="cpuPercent">The CPU usage limit as a percentage of total CPU capacity.</param>
    /// <param name="memoryLimitBytes">The memory usage limit in bytes.</param>
    /// <param name="name">The name of the job object.</param>
    /// <param name="killOnClose">Indicates whether the job should be terminated when the handle is closed.</param>
    /// <exception cref="PlatformNotSupportedException">Thrown if the operating system is not Windows.</exception>
    /// <exception cref="Win32Exception">Thrown if the creation or configuration of the job object fails.</exception>
    public WindowsWorkerJob(int cpuPercent, long memoryLimitBytes, string? name = null, bool killOnClose = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The local worker supervisor requires Windows Job Objects.");
        
        _handle = CreateJobObjectW(IntPtr.Zero, name);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        
        try
        {
            var limits = new WorkerJobExtendedLimits
            {
                Basic = new WorkerJobBasicLimits { Flags = (killOnClose ? 0x2000u : 0u) | 0x100 | 0x200 }, // durable agent jobs omit kill-on-close
                ProcessMemoryLimit = (nuint)memoryLimitBytes,
                JobMemoryLimit = (nuint)memoryLimitBytes,
            };
            Apply(9, limits);
            Apply(15, new WorkerJobCpuLimits { Flags = 1 | 4, CpuRate = (uint)(cpuPercent * 100) });
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Assigns the specified process to this job object.
    /// </summary>
    /// <param name="process">The process to assign to the job object.</param>
    /// <exception cref="Win32Exception">Thrown if the process cannot be assigned to the job object.</exception>
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot assign the owned worker to its resource-limited job.");
    }

    internal SafeFileHandle Handle => _handle;

    /// <summary>
    /// Tries to open an existing job object with the specified name.
    /// </summary>
    /// <param name="name">The name of the job object to open.</param>
    /// <returns>The <see cref="WindowsWorkerJob"/> instance if the job object exists; otherwise, <c>null</c>.</returns>
    public static WindowsWorkerJob? TryOpen(string name)
    {
        // JOB_OBJECT_QUERY is required by IsProcessInJob. SYNCHRONIZE alone cannot verify membership.
        SafeFileHandle handle = OpenJobObjectW(0x0004 | 0x00100000, false, name);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new WindowsWorkerJob(handle);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsWorkerJob"/> class with the specified job object handle.
    /// </summary>
    /// <param name="handle">The handle to the existing job object.</param>
    private WindowsWorkerJob(SafeFileHandle handle)
        => _handle = handle;

    /// <summary>
    /// Determines whether the specified process is contained within this job object.
    /// </summary>
    /// <param name="process">The process to check for membership in the job object.</param>
    /// <returns><c>true</c> if the process is contained within the job object; otherwise, <c>false</c>.</returns>
    public bool Contains(Process process)
        => IsProcessInJob(process.Handle, _handle, out bool result) && result;

    /// <summary>
    /// Applies the specified information to the job object using the given information class and value.
    /// </summary>
    /// <typeparam name="T">The type of the value to apply to the job object.</typeparam>
    /// <param name="informationClass">The information class specifying the type of information to set.</param>
    /// <param name="value">The value to apply to the job object.</param>
    /// <exception cref="Win32Exception">Thrown if the operation fails.</exception>
    private unsafe void Apply<T>(int informationClass, T value) where T : unmanaged
    {
        if (!SetInformationJobObject(_handle, informationClass, &value, (uint)sizeof(T)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Releases all resources used by the <see cref="WindowsWorkerJob"/> instance.
    /// </summary>
    public void Dispose()
        => _handle.Dispose();

    /// <summary>
    /// Terminates the job object with the specified exit code.
    /// </summary>
    /// <param name="exitCode">The exit code to use when terminating the job object.</param>
    /// <exception cref="Win32Exception">Thrown if the operation fails.</exception>
    public void Terminate(uint exitCode = 1)
    {
        if (!_handle.IsClosed && !TerminateJobObject(_handle, exitCode))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot terminate the owned worker job.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObjectW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr processHandle, SafeFileHandle jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool SetInformationJobObject(SafeFileHandle job, int informationClass, void* information, uint length);
}
