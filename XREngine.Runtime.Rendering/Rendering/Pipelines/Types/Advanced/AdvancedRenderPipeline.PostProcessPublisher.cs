using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.PostProcessing;
using static XREngine.RuntimeEngine.Rendering.State;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    /// <summary>
    /// Publishes the main post-process inputs through typed publishers, so the
    /// backend keeps the captured binding snapshot while the inputs stay the same.
    /// Some settings read state that has no generation: depth fog reads the camera,
    /// volumetric fog reads the world's fog volumes, the atmosphere reads the camera
    /// position, and CPU auto exposure changes every frame. While one of these is
    /// active, the generation advances every render frame, so the values are
    /// published again each frame, as with a uniform callback.
    /// </summary>
    private sealed class PostProcessBindingPublisher(AdvancedRenderPipeline owner, XRMaterial material)
        : IRenderResourceBindingPublisher
    {
        private readonly RenderBindingGenerationTracker<PostProcessBindingKey> _generation = new();

        public ERenderBindingFrequency Frequency => ERenderBindingFrequency.Pass;

        public ulong Generation => _generation.Advance(CaptureKey());

        public ulong ResourceGeneration => Generation;

        public void PublishUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => owner.ApplyPostProcessUniformValues(materialProgram, frozenView: null);

        public void PublishResources(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => owner.BindCurrentPostProcessTextures(material, materialProgram);

        private PostProcessBindingKey CaptureKey()
        {
            PipelinePostProcessState? state = ResolveCurrentSettingsCamera()?.GetActivePostProcessState();
            VignetteSettings? vignette = GetSettings<VignetteSettings>(state);
            ColorGradingSettings? color = GetSettings<ColorGradingSettings>(state);
            ChromaticAberrationSettings? chroma = GetSettings<ChromaticAberrationSettings>(state);
            FogSettings? fog = GetSettings<FogSettings>(state);
            AtmosphericScatteringSettings? atmosphere = GetSettings<AtmosphericScatteringSettings>(state);
            VolumetricFogSettings? volumetricFog = GetSettings<VolumetricFogSettings>(state);
            BloomSettings? bloom = GetSettings<BloomSettings>(state);
            TonemappingSettings? tonemapping = GetSettings<TonemappingSettings>(state);

            AtmosphericScatteringSettings resolvedAtmosphere = atmosphere ?? AtmosphericScatteringSettings.Default;
            bool cpuAutoExposure = color is { AutoExposure: true } && !color.UseGpuAutoExposureThisFrame;
            bool perFrameInputs =
                (fog?.DepthFogIntensity ?? 0.0f) > 0.0f ||
                volumetricFog is { Enabled: true, Intensity: > 0.0f, MaxDistance: > 0.0f } ||
                (resolvedAtmosphere.Enabled && resolvedAtmosphere.MaxDistance > 0.0f) ||
                cpuAutoExposure;

            var prefs = RuntimeEngine.EditorPreferences;
            bool webActive = Shaders.Compilation.WebPipelineRasterProgram.IsActive;
            // Browser builds substitute web post inputs that this key does not track.
            perFrameInputs |= webActive;
            XRRenderPipelineInstance? instance = CurrentRenderingPipeline;
            return new PostProcessBindingKey(
                perFrameInputs ? RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId : 0UL,
                instance?.ResourceGeneration ?? 0,
                new PostProcessTextureKey(
                    GetTexture<XRTexture>(HDRSceneTextureName),
                    GetTexture<XRTexture>(BloomBlurTextureName),
                    GetTexture<XRTexture>(DepthViewTextureName),
                    GetTexture<XRTexture>(StencilViewTextureName),
                    GetTexture<XRTexture>(AutoExposureTextureName),
                    GetTexture<XRTexture>(AtmosphereColorTextureName),
                    GetTexture<XRTexture>(VolumetricFogColorTextureName),
                    GetTexture<XRTexture>(AdvancedVisibilityResourceNames.Metadata)),
                new PostProcessSettingsKey(
                    vignette, vignette?.BindingGeneration ?? 0UL,
                    color, color?.BindingGeneration ?? 0UL,
                    chroma, chroma?.BindingGeneration ?? 0UL,
                    fog, fog?.BindingGeneration ?? 0UL,
                    atmosphere, atmosphere?.BindingGeneration ?? 0UL,
                    volumetricFog, volumetricFog?.BindingGeneration ?? 0UL,
                    bloom, bloom?.BindingGeneration ?? 0UL,
                    tonemapping, tonemapping?.BindingGeneration ?? 0UL),
                ResolveLensDistortionInputs(state),
                color?.UseGpuAutoExposureThisFrame ?? false,
                RuntimeEngine.Rendering.Settings.DefaultLuminance,
                ResolveOutputHDR(),
                prefs,
                prefs is null ? default : new Vector3((float)prefs.HoverOutlineColor.R, (float)prefs.HoverOutlineColor.G, (float)prefs.HoverOutlineColor.B),
                prefs is null ? default : new Vector3((float)prefs.SelectionOutlineColor.R, (float)prefs.SelectionOutlineColor.G, (float)prefs.SelectionOutlineColor.B),
                RuntimeRenderingHostServices.DebugDrawing.HoverOutlineEnabled ||
                    RuntimeRenderingHostServices.DebugDrawing.SelectionOutlineEnabled,
                webActive,
                webActive && GetTexture<XRTexture>(BloomBlurTextureName) is null,
                instance is not null && GlobalIlluminationDiagnosticPresentation.IsCurrentFrame(instance));
        }
    }

    private readonly record struct PostProcessTextureKey(
        XRTexture? HdrScene,
        XRTexture? BloomBlur,
        XRTexture? DepthView,
        XRTexture? StencilView,
        XRTexture? AutoExposure,
        XRTexture? AtmosphereColor,
        XRTexture? VolumetricFogColor,
        XRTexture? VisibilityMetadata);

    private readonly record struct PostProcessSettingsKey(
        VignetteSettings? Vignette, ulong VignetteGeneration,
        ColorGradingSettings? Color, ulong ColorGeneration,
        ChromaticAberrationSettings? Chroma, ulong ChromaGeneration,
        FogSettings? Fog, ulong FogGeneration,
        AtmosphericScatteringSettings? Atmosphere, ulong AtmosphereGeneration,
        VolumetricFogSettings? VolumetricFog, ulong VolumetricFogGeneration,
        BloomSettings? Bloom, ulong BloomGeneration,
        TonemappingSettings? Tonemapping, ulong TonemappingGeneration);

    private readonly record struct PostProcessBindingKey(
        ulong PerFrameStamp,
        int PipelineResourceGeneration,
        PostProcessTextureKey Textures,
        PostProcessSettingsKey Settings,
        LensDistortionInputs Lens,
        bool UseGpuAutoExposure,
        Vector3 DefaultLuminance,
        bool OutputHdr,
        object? EditorPreferences,
        Vector3 HoverOutlineColor,
        Vector3 SelectionOutlineColor,
        bool EditorOutlineEnabled,
        bool WebActive,
        bool WebBloomMissing,
        bool GlobalIlluminationDiagnostic);
}
