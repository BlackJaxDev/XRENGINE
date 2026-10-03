using XREngine.Data;
using XREngine.Data.Profiling;

namespace XREngine.Rendering;

/// <summary>Installs optional desktop hardware services without probing devices at registration.</summary>
public static class DesktopDiagnosticsBackend
{
    public static void Register()
    {
        Compression.NvCompBackend = new NvCompHardwareCodec();
        HardwareInventoryServices.Current = new WindowsHardwareInventory();
    }
}
