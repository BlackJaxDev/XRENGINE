using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace XREngine.ControlPlane.Service;

/// <summary>Atomically creates a process inside its job, inheriting only its three redirected streams.</summary>
internal static class WindowsContainedProcess
{
    /// <summary>
    /// Starts a new process inside the specified job, redirecting its standard input, output, and error streams.
    /// </summary>
    /// <param name="start">The <see cref="ProcessStartInfo"/> containing the start information for the new process.</param>
    /// <param name="job">The <see cref="WindowsWorkerJob"/> instance representing the job to contain the new process.</param>
    /// <param name="stdout">The <see cref="StreamReader"/> for reading the standard output of the new process.</param>
    /// <param name="stderr">The <see cref="StreamReader"/> for reading the standard error of the new process.</param>
    /// <returns>The <see cref="Process"/> instance representing the newly started process.</returns>
    public static unsafe Process Start(ProcessStartInfo start, WindowsWorkerJob job, out StreamReader stdout, out StreamReader stderr)
    {
        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        nint attributes = 0, environment = 0;
        bool initialized = false, jobReference = false;
        WorkerProcessInformation info = default;
        try
        {
            job.Handle.DangerousAddRef(ref jobReference);
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size));
            initialized = true;
            nint* streams = stackalloc nint[3]
            {
                input.ClientSafePipeHandle.DangerousGetHandle(),
                output.ClientSafePipeHandle.DangerousGetHandle(),
                error.ClientSafePipeHandle.DangerousGetHandle(),
            };
            nint jobHandle = job.Handle.DangerousGetHandle();
            // HANDLE_LIST and JOB_LIST are supported on the Windows 10 baseline.
            // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, streams, (nuint)(3 * sizeof(nint)), 0, 0));
            Check(UpdateProcThreadAttribute(attributes, 0, 0x2000D, &jobHandle, (nuint)sizeof(nint), 0, 0));
            var startup = new WorkerProcessStartupExtended
            {
                Startup = new()
                {
                    Size = (uint)sizeof(WorkerProcessStartupExtended), Flags = 0x100,
                    StandardInput = streams[0], StandardOutput = streams[1], StandardError = streams[2],
                },
                Attributes = attributes,
            };
            string block = string.Join('\0', start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            Check(CreateProcessW(start.FileName, new StringBuilder('"' + start.FileName + '"'), 0, 0, true,
                0x08000000 | 0x00000400 | 0x00080000, environment, start.WorkingDirectory, ref startup, out info));
            var process = Process.GetProcessById(checked((int)info.ProcessId));
            _ = process.Handle; // Acquire the owned handle while the original creation handle pins its identity.
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            input.DisposeLocalCopyOfClientHandle();
            stdout = new StreamReader(output, Encoding.UTF8);
            stderr = new StreamReader(error, Encoding.UTF8);
            return process;
        }
        catch
        {
            if (info.Process != 0)
                TerminateProcess(info.Process, 1);
            output.Dispose();
            error.Dispose();
            throw;
        }
        finally
        {
            if (info.Thread != 0) CloseHandle(info.Thread);
            if (info.Process != 0) CloseHandle(info.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (environment != 0) Marshal.FreeHGlobal(environment);
            if (jobReference) job.Handle.DangerousRelease();
        }
    }

    /// <summary>
    /// Checks the specified success flag and throws a <see cref="Win32Exception"/> if it is <c>false</c>.
    /// </summary>
    /// <param name="success">The success flag to check.</param>
    /// <exception cref="Win32Exception">Thrown if the success flag is <c>false</c>.</exception>
    private static void Check(bool success)
    {
        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, void* value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment,
        string directory, ref WorkerProcessStartupExtended startup, out WorkerProcessInformation information);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
