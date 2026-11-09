using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Win32 event and handle functions used by the DXGI flip-model present path.
/// </summary>
internal static partial class VulkanDxgiWin32
{
    /// <summary>Return value of <see cref="WaitForSingleObject"/> when the object is signaled.</summary>
    internal const uint WaitObject0 = 0x00000000;

    /// <summary>Return value of <see cref="WaitForSingleObject"/> when the timeout elapsed.</summary>
    internal const uint WaitTimeout = 0x00000102;

    /// <summary>Wait time that never elapses.</summary>
    internal const uint Infinite = 0xFFFFFFFF;

    /// <summary>Access right for every operation on a shared Direct3D object.</summary>
    internal const uint GenericAll = 0x10000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    internal static partial nint CreateEvent(nint eventAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>
    /// Converts a Vulkan acquire timeout in nanoseconds to a Win32 wait time in milliseconds.
    /// </summary>
    internal static uint ToWaitMilliseconds(ulong timeoutNanoseconds)
    {
        if (timeoutNanoseconds == ulong.MaxValue)
            return Infinite;

        ulong milliseconds = timeoutNanoseconds / 1_000_000UL;
        return milliseconds >= Infinite ? Infinite - 1 : (uint)milliseconds;
    }

    /// <summary>Closes a handle when it is not zero, then sets it to zero.</summary>
    internal static void CloseIfValid(ref nint handle)
    {
        if (handle == 0)
            return;

        CloseHandle(handle);
        handle = 0;
    }
}
