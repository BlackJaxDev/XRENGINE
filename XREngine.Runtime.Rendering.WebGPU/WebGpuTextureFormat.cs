using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Exact texture encodings and transfer shapes; no format widening or numeric reinterpretation.</summary>
internal static class WebGpuTextureFormat
{
    public static string Map(ESizedInternalFormat format) => format switch
    {
        ESizedInternalFormat.R8 => "r8unorm",
        ESizedInternalFormat.Rgba8 => "rgba8unorm",
        ESizedInternalFormat.Srgb8Alpha8 => "rgba8unorm-srgb",
        ESizedInternalFormat.R16f => "r16float",
        ESizedInternalFormat.Rg16f => "rg16float",
        ESizedInternalFormat.Rgba16f => "rgba16float",
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
    public static bool SupportsStorage(string format) => format is "rgba8unorm" or "rgba16float" or
        "r32float" or "rg32float" or "rgba32float" or "r32uint" or "rg32uint" or "rgba32uint" or
        "r32sint" or "rg32sint" or "rgba32sint";

    public static BrowserTextureUsage Usage(WebGpuRendererHost renderer, string format, uint samples, bool storage)
    {
        if (format == "depth32float-stencil8" && renderer.DeviceCapabilities?.Features.Contains("depth32float-stencil8") != true)
            throw Unsupported("depth32float-stencil8 requires the enabled selected-device depth32float-stencil8 feature");
        if (samples > 1 && (IsInteger(format) || format is "rg32float" or "rgba32float"))
            throw Unsupported($"format '{format}' does not support multisampling in WebGPU");
        if (storage && (samples != 1 || !SupportsStorage(format)))
            throw Unsupported($"format '{format}' with {samples} samples has no admitted storage-image encoding");
        BrowserTextureUsage usage = BrowserTextureUsage.TextureBinding | BrowserTextureUsage.RenderAttachment;
        if (samples == 1 && !IsDepth(format)) usage |= BrowserTextureUsage.CopySource | BrowserTextureUsage.CopyDestination;
        else if (samples == 1 && format == "depth32float") usage |= BrowserTextureUsage.CopySource;
        if (storage) usage |= BrowserTextureUsage.StorageBinding;
        return usage;
    }

    public static int UploadPixelBytes(string format, EPixelFormat pixel, EPixelType type)
    {
        (EPixelFormat Pixel, EPixelType Type, int Bytes) shape = format switch
        {
            "r8unorm" => (EPixelFormat.Red, EPixelType.UnsignedByte, 1),
            "rgba8unorm" or "rgba8unorm-srgb" => (EPixelFormat.Rgba, EPixelType.UnsignedByte, 4),
            "r16float" => (EPixelFormat.Red, EPixelType.HalfFloat, 2),
            "rg16float" => (EPixelFormat.Rg, EPixelType.HalfFloat, 4),
            "rgba16float" => (EPixelFormat.Rgba, EPixelType.HalfFloat, 8),
            "r32float" => (EPixelFormat.Red, EPixelType.Float, 4),
            "rg32float" => (EPixelFormat.Rg, EPixelType.Float, 8),
            "rgba32float" => (EPixelFormat.Rgba, EPixelType.Float, 16),
            "r32uint" => (EPixelFormat.RedInteger, EPixelType.UnsignedInt, 4),
            "rg32uint" => (EPixelFormat.RgInteger, EPixelType.UnsignedInt, 8),
            "rgba32uint" => (EPixelFormat.RgbaInteger, EPixelType.UnsignedInt, 16),
            "r32sint" => (EPixelFormat.RedInteger, EPixelType.Int, 4),
            "rg32sint" => (EPixelFormat.RgInteger, EPixelType.Int, 8),
            "rgba32sint" => (EPixelFormat.RgbaInteger, EPixelType.Int, 16),
            _ => throw Unsupported($"format '{format}' has no CPU mip upload encoding"),
        };
        if (pixel != shape.Pixel || type != shape.Type)
            throw Unsupported($"upload for '{format}' requires exact {shape.Pixel}/{shape.Type} bytes");
        return shape.Bytes;
    }

    private static NotSupportedException Unsupported(string reason) => new($"WebGPU.Texture.OperationUnsupported: {reason}.");
}
