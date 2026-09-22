using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    private readonly record struct LightCombineBindingState(
        int DeferredDebugMode,
        Vector3 GlobalAmbient,
        bool UseAmbientOcclusion,
        float AmbientOcclusionPower,
        bool AmbientOcclusionMultiBounce,
        bool SpecularOcclusionEnabled,
        bool RequiresProbeIblBindings,
        bool ReplacesProbeDiffuse,
        bool ProbeGiSamplingSuppressed,
        bool ProbeBindingResourcesEnabled,
        bool ProbeBindingUseGrid,
        int ProbeBindingProbeCount,
        int ProbeBindingTetraCount,
        Vector3 ProbeGridOrigin,
        float ProbeGridCellSize,
        IVector3 ProbeGridDimensions,
        XRTexture? BrdfTexture,
        XRTexture? EmissionTexture,
        XRTexture2DArray? ProbeIrradianceArray,
        XRTexture2DArray? ProbePrefilterArray,
        XRDataBuffer? ProbePositionBuffer,
        XRDataBuffer? ProbeParamBuffer,
        XRDataBuffer? ProbeTetraBuffer,
        XRDataBuffer? ProbeGridCellBuffer,
        XRDataBuffer? ProbeGridIndexBuffer,
        XRTexture DummyBrdfTexture,
        XRTexture2DArray DummyPbrTextureArray,
        ulong DebugProbeTetrahedraFrame,
        int PipelineResourceGeneration);

    /// <summary>
    /// Owns both halves of the deferred combine publication. Exact references
    /// and values are compared before a Vulkan artifact may be reused.
    /// </summary>
    private sealed class LightCombineBindingPublisher(
        DefaultRenderPipeline owner) :
        IRenderResourceBindingPublisher,
        IPersistentProgramBindingRequirementOwner
    {
        private readonly object _generationSync = new();
        private LightCombineBindingState _lastState;
        private bool _hasLastState;
        private ulong _generation = 1;

        public ERenderBindingFrequency Frequency
            => ERenderBindingFrequency.Pass;

        public ulong Generation
        {
            get
            {
                LightCombineBindingState state =
                    owner.CaptureLightCombineBindingState();
                lock (_generationSync)
                {
                    if (_hasLastState && state == _lastState)
                        return _generation;

                    _lastState = state;
                    _hasLastState = true;
                    unchecked { _generation++; }
                    if (_generation == 0)
                        _generation = 1;
                    return _generation;
                }
            }
        }

        public ulong ResourceGeneration => Generation;

        public EUniformRequirements OwnedPersistentArtifactRequirement
            => EUniformRequirements.AmbientOcclusion;

        public void PublishUniforms(
            XRRenderProgram vertexProgram,
            XRRenderProgram materialProgram)
            => owner.ApplyLightCombineNumericBindings(materialProgram);

        public void PublishResources(
            XRRenderProgram vertexProgram,
            XRRenderProgram materialProgram)
        {
            owner.BindPbrLightingResources(materialProgram, deferredProbeBufferBindings: true);
            XRTexture? emission = GetTexture<XRTexture>(
                RuntimeEnableMsaaDeferred ? MsaaEmissionColorTextureName : EmissionColorTextureName);
            if (emission is not null)
                materialProgram.Sampler(EmissionColorTextureName, emission, 9);
        }
    }

    private LightCombineBindingState CaptureLightCombineBindingState()
    {
        XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        bool ownsCurrentInstance = instance is not null && ReferenceEquals(instance.Pipeline, this);
        ForwardLightProbeInstanceResources? probeState = ownsCurrentInstance &&
            ForwardLightProbeInstanceResources.TryGet(instance, out ForwardLightProbeInstanceResources? existingState)
                ? existingState
                : null;
        XRTexture? brdfTexture = GetTexture<XRTexture>(BRDFTextureName);
        bool probeGiSamplingSuppressed =
            IsProbeGiSamplingSuppressedForCurrentPass();
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (GlobalIlluminationPlan.RequiresNativeProbeIblBindings &&
            !probeGiSamplingSuppressed &&
            ownsCurrentInstance &&
            probeState?.BindingStateFrameId != frameId)
        {
            SyncPbrLightingResourcesForFrame(brdfTexture);
            ForwardLightProbeInstanceResources.TryGet(instance, out probeState);
        }

        bool useAmbientOcclusion = ShouldUseAmbientOcclusion();
        float ambientOcclusionPower = 1.0f;
        bool ambientOcclusionMultiBounce = false;
        bool specularOcclusionEnabled = false;
        AmbientOcclusionSettings? aoSettings =
            ResolveAmbientOcclusionSettings();
        if (aoSettings is not null)
        {
            ambientOcclusionPower = aoSettings.Power;
            if (AmbientOcclusionSettings.NormalizeType(aoSettings.Type) ==
                AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion)
            {
                ambientOcclusionMultiBounce =
                    aoSettings.GroundTruth.MultiBounceEnabled;
                specularOcclusionEnabled =
                    aoSettings.GroundTruth.SpecularOcclusionEnabled;
            }
        }

        bool renderProbeTetrahedra =
            RuntimeEngine.EditorPreferences.Debug.RenderLightProbeTetrahedra &&
            probeState is { BindingResourcesEnabled: true, BindingTetraCount: > 0 };
        return new LightCombineBindingState(
            ResolveDeferredDebugMode(),
            ResolveGlobalAmbient(),
            useAmbientOcclusion,
            ambientOcclusionPower,
            ambientOcclusionMultiBounce,
            specularOcclusionEnabled,
            GlobalIlluminationPlan.RequiresNativeProbeIblBindings,
            GlobalIlluminationPlan.ReplacesProbeDiffuse,
            probeGiSamplingSuppressed,
            probeState?.BindingResourcesEnabled ?? false,
            probeState?.BindingUseGrid ?? false,
            probeState?.BindingProbeCount ?? 0,
            probeState?.BindingTetraCount ?? 0,
            probeState?.GridOrigin ?? Vector3.Zero,
            probeState?.GridCellSize ?? 0.0f,
            probeState?.GridDimensions ?? IVector3.Zero,
            brdfTexture,
            GetTexture<XRTexture>(RuntimeEnableMsaaDeferred ? MsaaEmissionColorTextureName : EmissionColorTextureName),
            probeState?.IrradianceArray,
            probeState?.PrefilterArray,
            probeState?.PositionBuffer,
            probeState?.ParamBuffer,
            probeState?.TetraBuffer,
            probeState?.GridCellBuffer,
            probeState?.GridIndexBuffer,
            Lights3DCollection.DummyShadowMap,
            Lights3DCollection.DummyPbrTextureArray,
            renderProbeTetrahedra ? frameId : 0UL,
            ownsCurrentInstance ? instance!.ResourceGeneration : 0);
    }
}
