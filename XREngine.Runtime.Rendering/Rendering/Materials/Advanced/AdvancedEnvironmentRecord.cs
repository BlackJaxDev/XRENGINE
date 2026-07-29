using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Global environment maps and exposure state.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedEnvironmentRecord
{
    public uint StableEnvironmentId;
    public uint Generation;
    public uint Flags;
    public uint Priority;

    public AdvancedTextureReference Environment;
    public AdvancedTextureReference Irradiance;
    public AdvancedTextureReference PrefilteredRadiance;
    public AdvancedTextureReference BrdfLut;

    public Vector4 RotationAndExposure;
    public Vector4 AmbientColorAndIntensity;

    public uint ViewMaskLo;
    public uint ViewMaskHi;
    public uint Reserved0;
    public uint Reserved1;
}
