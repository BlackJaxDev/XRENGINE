using System.Runtime.InteropServices;

namespace XREngine.UnitTests.SoftwareVulkan;

/// <summary>Sets the native loader's environment before any Vulkan entry point is loaded.</summary>
internal static class SoftwareVulkanEnvironment
{
    public static void SelectDriver(string path)
    {
        Set("VK_DRIVER_FILES", path);
        Set("VK_ICD_FILENAMES", path);
    }

    private static void Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        // On Unix, managed environment updates need not reach libc getenv used by the loader.
        if (OperatingSystem.IsLinux() && SetEnvironment(name, value, 1) != 0)
            throw new InvalidOperationException($"Cannot set native Vulkan loader variable {name}.");
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Software Vulkan validation supports Linux and Windows.");
    }

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetEnvironment(string name, string value, int overwrite);
}
