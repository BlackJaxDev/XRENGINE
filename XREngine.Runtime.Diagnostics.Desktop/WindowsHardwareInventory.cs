using System.Management;
using XREngine.Data.Profiling;

namespace XREngine.Rendering;

/// <summary>Reads the active Windows graphics adapters through WMI.</summary>
public sealed class WindowsHardwareInventory : IHardwareInventory
{
    public bool TryGetActiveGpuCount(out int count, out string? diagnostic)
    {
        count = 0;
        diagnostic = null;
        if (!OperatingSystem.IsWindows())
        {
            diagnostic = "Windows hardware inventory is unavailable on this platform.";
            return false;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController where Status='OK'");
            using var results = searcher.Get();
            foreach (ManagementBaseObject adapter in results)
            {
                using (adapter)
                    count++;
            }
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = ex.Message;
            return false;
        }
    }
}
