using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private XRTexture2D? _disabledAmbientOcclusion;
    private ObjectCacheOwnership? _disabledAmbientOcclusionOwnership;

    /// <summary>Publishes generation-owned GTAO visibility, or a neutral renderer-owned binding when AO is disabled.</summary>
    internal void PublishAmbientOcclusion(WebGpuRenderProgram program)
    {
        if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline is not DefaultRenderPipeline pipeline)
            throw new NotSupportedException("WebGPU.AmbientOcclusion.PipelineUnsupported: standard lit AO requires the active default render pipeline.");

        bool enabled = pipeline.TryGetWebAmbientOcclusion(out XRTexture2D? finalAo,
            out float power, out bool multiBounce);
        XRTexture2D texture = enabled
            ? finalAo ?? throw new InvalidOperationException("WebGPU.AmbientOcclusion.FinalMissing: enabled GTAO has no committed final visibility texture.")
            : EnsureDisabledAmbientOcclusion();
        program.SetVector4("AmbientOcclusionControls", enabled
            ? new Vector4(1, power, multiBounce ? 1 : 0, 0)
            : new Vector4(0, 1, 0, 0));
        program.Data.Sampler("AmbientOcclusionTexture", texture, 0);
    }

    private XRTexture2D EnsureDisabledAmbientOcclusion()
    {
        if (_disabledAmbientOcclusion is not null)
            return _disabledAmbientOcclusion;

        using ObjectCachePublicationScope ownership = XRObjectBase.BeginIndependentObjectCachePublication();
        using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        XRTexture2D texture = new(1u, 1u, ColorF4.White)
        {
            Name = "Disabled ambient occlusion visibility",
            SamplerName = "AmbientOcclusionTexture",
            AutoGenerateMipmaps = false,
            Resizable = false,
            MinFilter = ETexMinFilter.Nearest,
            MagFilter = ETexMagFilter.Nearest,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
            MaxAnisotropy = 1,
            MinLOD = 0,
            MaxLOD = 0,
        };
        SetField(ref _disabledAmbientOcclusionOwnership, ownership.CompleteWithOwnership());
        SetField(ref _disabledAmbientOcclusion, texture);
        return texture;
    }

    private void DestroyAmbientOcclusionDefaults()
    {
        _disabledAmbientOcclusionOwnership?.Dispose();
        SetField(ref _disabledAmbientOcclusionOwnership, null);
        SetField(ref _disabledAmbientOcclusion, null);
    }
}
