using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    /// <summary>
    /// Selects portable physical storage formats while retaining the Advanced
    /// logical channel, precision, and quantization contracts.
    /// </summary>
    private static void LowerWebStorageFormat(ref EPixelInternalFormat internalFormat,
        ref EPixelFormat pixelFormat, ref EPixelType pixelType, ref ESizedInternalFormat sizedFormat)
    {
        if (!OperatingSystem.IsBrowser() && AbstractRenderer.Current?.BackendId != RendererBackendId.WebGPU)
            return;
        switch (sizedFormat)
        {
            case ESizedInternalFormat.R8:
                // Writers explicitly quantize normalized AO/reactive values to
                // UNORM8. The physical extra bytes do not add logical precision.
                internalFormat = EPixelInternalFormat.R32f;
                pixelFormat = EPixelFormat.Red;
                pixelType = EPixelType.Float;
                sizedFormat = ESizedInternalFormat.R32f;
                break;
            case ESizedInternalFormat.Rg16f:
                // Motion keeps its exact half-float XY values. Writers define
                // the newly physical ZW channels as zero.
                internalFormat = EPixelInternalFormat.Rgba16f;
                pixelFormat = EPixelFormat.Rgba;
                pixelType = EPixelType.HalfFloat;
                sizedFormat = ESizedInternalFormat.Rgba16f;
                break;
        }
    }
}
