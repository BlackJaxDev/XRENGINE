using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Unified native-shading light record.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedLightRecord
{
    public uint StableLightId;
    public uint Generation;
    public EAdvancedLightType Type;
    public EAdvancedLightRecordFlags Flags;

    public Vector4 PositionAndRadius;
    public Vector4 DirectionAndOuterCone;
    public Vector4 ColorAndIntensity;
    public Vector4 ShapeAndInnerCone;

    public AdvancedTextureReference CookieTexture;
    public AdvancedGpuHandle ShadowRecord;
    public uint LayerMask;
    public uint ViewMaskLo;

    public uint ViewMaskHi;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
}
