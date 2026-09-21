using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>416-byte std430 material row; texture roles remain independent of raster shader slots.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DDGIMaterialGpu
{
    public Vector4 BaseColorOpacity;
    public Vector4 EmissiveMetallic;
    public Vector4 Surface; // roughness, normal scale, alpha cutoff, alpha mode (opaque/mask/blend)
    public Vector4 Transmission;
    public Vector4 EmissionOptions; // x: legacy emission follows the albedo texture
    public DDGIMaterialMapGpu BaseColorMap;
    public DDGIMaterialMapGpu OpacityMap;
    public DDGIMaterialMapGpu NormalMap;
    public DDGIMaterialMapGpu MetallicMap;
    public DDGIMaterialMapGpu RoughnessMap;
    public DDGIMaterialMapGpu EmissiveMap;
    public DDGIMaterialMapGpu TransmissionMap;

    public static DDGIMaterialGpu Default => new()
    {
        BaseColorOpacity = Vector4.One,
        Surface = new Vector4(1, 1, 0.5f, 0),
        Transmission = new Vector4(1, 1, 1, 0),
        BaseColorMap = DDGIMaterialMapGpu.None,
        OpacityMap = DDGIMaterialMapGpu.None,
        NormalMap = DDGIMaterialMapGpu.None,
        MetallicMap = DDGIMaterialMapGpu.None,
        RoughnessMap = DDGIMaterialMapGpu.None,
        EmissiveMap = DDGIMaterialMapGpu.None,
        TransmissionMap = DDGIMaterialMapGpu.None,
    };
}
