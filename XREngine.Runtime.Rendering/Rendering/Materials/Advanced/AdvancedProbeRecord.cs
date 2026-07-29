using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Reflection and irradiance probe influence record.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedProbeRecord
{
    public uint StableProbeId;
    public uint Generation;
    public EAdvancedProbeType Type;
    public uint Flags;

    public Vector4 PositionAndRadius;
    public Vector4 InfluenceInner;
    public Vector4 InfluenceOuter;
    public Vector4 InfluenceOffsetAndShape;
    public Vector4 ProxyCenterAndEnable;
    public Vector4 ProxyHalfExtents;
    public Vector4 ProxyRotation;

    public AdvancedTextureReference Irradiance;
    public AdvancedTextureReference PrefilteredRadiance;

    public uint ViewMaskLo;
    public uint ViewMaskHi;
    public uint Priority;
    public uint Reserved;
}
