using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLBakedDataIdentifier
{
    public IPLBakedDataType type;
    public IPLBakedDataVariation variation;
    public IPLSphere endpointInfluence;
}