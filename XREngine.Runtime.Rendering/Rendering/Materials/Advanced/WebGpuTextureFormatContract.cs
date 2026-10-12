using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Exact shared WebGPU texture encodings used by cold admission and runtime allocation.</summary>
public static class WebGpuTextureFormatContract
{
    public static string Map(ESizedInternalFormat format) => format switch
    {
        ESizedInternalFormat.R8 => "r8unorm",
        ESizedInternalFormat.Rgba8 => "rgba8unorm",
        ESizedInternalFormat.Srgb8Alpha8 => "rgba8unorm-srgb",
        ESizedInternalFormat.R16f => "r16float",
        ESizedInternalFormat.Rg16f => "rg16float",
        ESizedInternalFormat.Rgba16f => "rgba16float",
        ESizedInternalFormat.Rgba16ui => "rgba16uint",
        ESizedInternalFormat.R32f => "r32float",
        ESizedInternalFormat.Rg32f => "rg32float",
        ESizedInternalFormat.Rgba32f => "rgba32float",
        ESizedInternalFormat.R32ui => "r32uint",
        ESizedInternalFormat.Rg32ui => "rg32uint",
        ESizedInternalFormat.Rgba32ui => "rgba32uint",
        ESizedInternalFormat.R32i => "r32sint",
        ESizedInternalFormat.Rg32i => "rg32sint",
        ESizedInternalFormat.Rgba32i => "rgba32sint",
        ESizedInternalFormat.DepthComponent16 => "depth16unorm",
        ESizedInternalFormat.DepthComponent24 => "depth24plus",
        ESizedInternalFormat.DepthComponent32f => "depth32float",
        ESizedInternalFormat.Depth24Stencil8 => "depth24plus-stencil8",
        ESizedInternalFormat.Depth32fStencil8 => "depth32float-stencil8",
        _ => throw Unsupported($"format '{format}' has no exact admitted WebGPU encoding"),
    };

    public static bool IsDepth(string format) => format.StartsWith("depth", StringComparison.Ordinal);
    public static bool HasStencil(string format) => format is "depth24plus-stencil8" or "depth32float-stencil8";
    public static bool IsInteger(string format) => format.EndsWith("uint", StringComparison.Ordinal) || format.EndsWith("sint", StringComparison.Ordinal);
    private static NotSupportedException Unsupported(string reason) => new($"WebGPU.Texture.OperationUnsupported: {reason}.");
}
