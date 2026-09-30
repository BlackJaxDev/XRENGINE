using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLSourceSettings
{
    public IPLSimulationFlags flags;
}
