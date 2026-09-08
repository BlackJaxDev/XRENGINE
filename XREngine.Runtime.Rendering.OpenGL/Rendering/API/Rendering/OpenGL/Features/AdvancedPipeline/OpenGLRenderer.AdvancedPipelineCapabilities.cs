namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    /// <inheritdoc />
    public override AdvancedRenderPipelineCapabilities GetAdvancedRenderPipelineCapabilities()
    {
        if (!_advancedAdmissionReady && RuntimeEngine.IsRenderThread)
            _ = GetAdvancedVisibilityFamilyAdmission();
        EAdvancedIndirectSubmissionMode indirectSubmission = SupportsIndirectCountDraw()
                ? EAdvancedIndirectSubmissionMode.MultiDrawIndirectCount
                : EAdvancedIndirectSubmissionMode.MultiDrawIndirect;

        EAdvancedTextureIndirectionMode textureIndirection = SupportsBindlessTextureHandles
            ? EAdvancedTextureIndirectionMode.OpenGlBindlessHandles
            : EAdvancedTextureIndirectionMode.TextureArray;

        return new(
            Backend: RuntimeGraphicsApiKind.OpenGL,
            RendererAvailable: true,
            SupportsIntegerRenderTargets: true,
            VisibilityTargetEncoding: EAdvancedVisibilityTargetEncoding.R32G32UInt,
            SupportsComputeShaders: true,
            SupportsStorageBuffers: true,
            IndirectSubmission: indirectSubmission,
            TextureIndirection: textureIndirection,
            Synchronization: EAdvancedSynchronizationMode.OpenGlMemoryBarrier,
            SupportsFrameSlotStorage: true,
            SupportsStereoArrayResources: true,
            ShaderFamily: _advancedAdmissionReady ? EAdvancedShaderFamily.VisibilityBuffer : EAdvancedShaderFamily.None,
            SupportsBufferDeviceAddress: false,
            SupportsDescriptorIndexing: false,
            SupportsDescriptorHeap: false,
            SupportsSubgroupOperations: false,
            SupportsMeshShaders: false,
            SupportsAsyncCompute: false,
            SupportsTimelineSemaphores: false,
            SupportsOpenGlMultiviewRaster: RuntimeEngine.IsRenderThread
                ? _advancedAdmissionReady && TryEnsureAdvancedStereoPrograms(out _)
                : _advancedStereoReady);
    }
}
