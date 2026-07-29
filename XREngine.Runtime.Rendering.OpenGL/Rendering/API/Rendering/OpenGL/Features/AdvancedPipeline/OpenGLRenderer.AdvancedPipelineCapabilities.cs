namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    /// <inheritdoc />
    public override AdvancedRenderPipelineCapabilities GetAdvancedRenderPipelineCapabilities()
    {
        EAdvancedIndirectSubmissionMode indirectSubmission = SupportsMeshletDispatch()
            ? EAdvancedIndirectSubmissionMode.MeshTasksIndirectCount
            : SupportsIndirectCountDraw()
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
            // The frame contract exists, but production visibility shaders do not.
            // Keep selection unavailable until the backend implements the full family.
            ShaderFamily: EAdvancedShaderFamily.None,
            SupportsBufferDeviceAddress: false,
            SupportsDescriptorIndexing: false,
            SupportsDescriptorHeap: false,
            SupportsSubgroupOperations: false,
            SupportsMeshShaders: SupportsMeshletDispatch(),
            SupportsAsyncCompute: false,
            SupportsTimelineSemaphores: false);
    }
}
