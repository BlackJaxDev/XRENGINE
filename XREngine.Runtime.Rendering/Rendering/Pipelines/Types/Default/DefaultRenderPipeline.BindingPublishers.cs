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
        bool UsesLightProbeGi,
        bool ProbeGiSamplingSuppressed,
        bool ProbeBindingResourcesEnabled,
        bool ProbeBindingUseGrid,
        int ProbeBindingProbeCount,
        int ProbeBindingTetraCount,
        Vector3 ProbeGridOrigin,
        float ProbeGridCellSize,
        IVector3 ProbeGridDimensions,
        XRTexture? BrdfTexture,
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
            => owner.BindPbrLightingResources(
                materialProgram,
                deferredProbeBufferBindings: true);
    }

    private LightCombineBindingState CaptureLightCombineBindingState()
    {
        XRTexture? brdfTexture = GetTexture<XRTexture>(BRDFTextureName);
        bool probeGiSamplingSuppressed =
            IsProbeGiSamplingSuppressedForCurrentPass();
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (UsesLightProbeGI &&
            !probeGiSamplingSuppressed &&
            _probeBindingStateFrameId != frameId)
        {
            SyncPbrLightingResourcesForFrame(brdfTexture);
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
            _probeBindingResourcesEnabled &&
            _probeBindingTetraCount > 0;
        return new LightCombineBindingState(
            ResolveDeferredDebugMode(),
            ResolveGlobalAmbient(),
            useAmbientOcclusion,
            ambientOcclusionPower,
            ambientOcclusionMultiBounce,
            specularOcclusionEnabled,
            UsesLightProbeGI,
            probeGiSamplingSuppressed,
            _probeBindingResourcesEnabled,
            _probeBindingUseGrid,
            _probeBindingProbeCount,
            _probeBindingTetraCount,
            _probeGridOrigin,
            _probeGridCellSize,
            _probeGridDims,
            brdfTexture,
            _probeIrradianceArray,
            _probePrefilterArray,
            _probePositionBuffer,
            _probeParamBuffer,
            _probeTetraBuffer,
            _probeGridCellBuffer,
            _probeGridIndexBuffer,
            Lights3DCollection.DummyShadowMap,
            Lights3DCollection.DummyPbrTextureArray,
            renderProbeTetrahedra ? frameId : 0UL,
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline
                ?.ResourceGeneration ?? 0);
    }
}
