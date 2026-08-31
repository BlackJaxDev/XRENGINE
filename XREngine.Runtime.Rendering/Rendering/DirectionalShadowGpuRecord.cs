using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Vectors;

namespace XREngine.Rendering;

/// <summary>
/// One immutable directional-shadow cascade payload shared by forward, deferred,
/// enhanced, and fog lighting. Its offsets mirror <c>DirectionalShadowGpuRecord</c>
/// in the GLSL storage block exactly.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 224)]
public struct DirectionalShadowGpuRecord
{
    [FieldOffset(0)] public Matrix4x4 CurrentWorldToLight;
    [FieldOffset(64)] public Matrix4x4 RenderedWorldToLight;
    [FieldOffset(128)] public Vector4 CurrentSplitBlendBias;
    [FieldOffset(144)] public Vector4 RenderedSplitBlendBias;
    [FieldOffset(160)] public Vector4 ReceiverOffsetsAge;
    [FieldOffset(176)] public IVector4 AtlasPacked0;
    [FieldOffset(192)] public Vector4 AtlasUvScaleBias;
    [FieldOffset(208)] public Vector4 AtlasDepthParams;
}
