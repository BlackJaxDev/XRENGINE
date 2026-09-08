using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    internal bool TryDispatchAdvancedNativeStage(in AdvancedVisibilityStageBackendRequest request, out string reason)
    {
        AdvancedVisibilityFamilyReservation reservation = request.Reservation;
        if (!TryCaptureAdvancedVisibilityOutputClosure(in request, out OpenGLAdvancedVisibilityOutputClosure closure, out reason) ||
            _advancedOutputRegistry is null ||
            !_advancedOutputRegistry.TryGetRetainedSlot(in reservation, out OpenGLAdvancedVisibilitySlot? slot) || slot is null ||
            !TryEnsureAdvancedStagePrograms(out reason))
            return false;

        OpenGLAdvancedNativeBufferStorage buffers = slot.NativeBuffers ??= new OpenGLAdvancedNativeBufferStorage(this);
        uint views = checked((uint)request.Views.ViewCount), depthSlices = Math.Max(1u, request.FroxelDepthSlices);
        buffers.Bind();
        if (buffers.PreparedRenderFrame != request.RenderFrameId || buffers.PreparedPublication != request.Publication.PublicationGeneration)
        {
            buffers.EnsureCapacity(closure.Width, closure.Height, views, depthSlices);
            buffers.UploadPushConstants(closure.Width, closure.Height, views, depthSlices, _advancedSceneUploader?.LightCount ?? 0u,
                request.RequireNativeOutput, request.EnableBuiltInAmbientOcclusion, request.EnableLightProbesAndIbl, request.ShadingDebugView);
            buffers.PreparedRenderFrame = request.RenderFrameId;
            buffers.PreparedPublication = request.Publication.PublicationGeneration;
        }
        Span<uint> priorSamplers = stackalloc uint[5];
        if (!TryBindAdvancedNativeSamplers(0u, priorSamplers, out reason))
            return false;
        try
        {
            BindNativeResources(in closure);
            uint tilesX = DivideRoundUp(closure.Width, 16u), tilesY = DivideRoundUp(closure.Height, 16u), view = request.NativeViewIndex;
            return request.Stage switch
            {
                EAdvancedRenderStage.AmbientOcclusion => DispatchAmbientOcclusion(buffers, view, tilesX, tilesY, out reason),
                EAdvancedRenderStage.WorkClassification => DispatchClassification(buffers, view, tilesX, tilesY, out reason),
                EAdvancedRenderStage.NativeOpaqueShading => DispatchNativeOpaque(buffers, view, tilesX, tilesY, depthSlices, out reason),
                _ => UnsupportedNativeStage(out reason),
            };
        }
        finally { RestoreAdvancedNativeSamplers(0u, priorSamplers); }
    }

    private void BindNativeResources(in OpenGLAdvancedVisibilityOutputClosure closure)
    {
        RawGL.BindTextureUnit(0u, closure.IdentityId); RawGL.BindTextureUnit(1u, closure.MetadataId);
        RawGL.BindTextureUnit(2u, closure.DepthId); RawGL.BindTextureUnit(3u, closure.AmbientOcclusionId);
        RawGL.BindImageTexture(0u, closure.HdrId, 0, true, 0, BufferAccessARB.WriteOnly, InternalFormat.Rgba16f);
        RawGL.BindImageTexture(1u, closure.VelocityId, 0, true, 0, BufferAccessARB.WriteOnly, InternalFormat.RG16f);
        RawGL.BindImageTexture(2u, closure.ReactiveMaskId, 0, true, 0, BufferAccessARB.WriteOnly, InternalFormat.R8);
        RawGL.BindImageTexture(3u, closure.ShadingDiagnosticsId, 0, true, 0, BufferAccessARB.WriteOnly, InternalFormat.R32ui);
        RawGL.BindImageTexture(4u, closure.AmbientOcclusionId, 0, true, 0, BufferAccessARB.WriteOnly, InternalFormat.R8);
    }

    private bool DispatchAmbientOcclusion(OpenGLAdvancedNativeBufferStorage buffers, uint view, uint tilesX, uint tilesY, out string reason)
    {
        if (GenericToAPI<GLRenderProgram>(_advancedGtaoProgram) is not { } program || !program.Use())
        { reason = "The OpenGL Advanced GTAO program is not linked."; return false; }
        buffers.BindPushConstants(view, 0u);
        RawGL.DispatchCompute(tilesX, tilesY, 1u);
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);
        reason = "Ready"; return true;
    }

    private bool DispatchClassification(OpenGLAdvancedNativeBufferStorage buffers, uint view, uint tilesX, uint tilesY, out string reason)
    {
        if (GenericToAPI<GLRenderProgram>(_advancedClassifyTilesProgram) is not { } classify ||
            GenericToAPI<GLRenderProgram>(_advancedBuildClassificationIndirectProgram) is not { } indirect || !classify.Use())
        { reason = "The OpenGL Advanced classification programs are not linked."; return false; }
        if (view == 0u) buffers.ResetClassification();
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        buffers.BindPushConstants(view, 0u);
        RawGL.DispatchCompute(tilesX, tilesY, 1u);
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        if (!indirect.Use()) { reason = "The OpenGL Advanced classification-indirect program is not linked."; return false; }
        buffers.BindPushConstants(view, 0u);
        RawGL.DispatchCompute(1u, 1u, 1u);
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.CommandBarrierBit);
        reason = "Ready"; return true;
    }

    private bool DispatchNativeOpaque(OpenGLAdvancedNativeBufferStorage buffers, uint view, uint tilesX, uint tilesY, uint depthSlices, out string reason)
    {
        if (GenericToAPI<GLRenderProgram>(_advancedBuildFroxelsProgram) is not { } froxels ||
            GenericToAPI<GLRenderProgram>(_advancedShadeBackgroundProgram) is not { } background ||
            GenericToAPI<GLRenderProgram>(_advancedShadeNativeOpaqueProgram) is not { } shade || !froxels.Use())
        { reason = "The OpenGL Advanced native opaque programs are not linked."; return false; }
        buffers.ResetLightingCounters();
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        buffers.BindPushConstants(view, 0u);
        RawGL.DispatchCompute(DivideRoundUp(tilesX, 8u), DivideRoundUp(tilesY, 8u), DivideRoundUp(depthSlices, 4u));
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        if (!background.Use()) { reason = "The OpenGL Advanced background program is not linked."; return false; }
        buffers.BindPushConstants(view, 0u);
        RawGL.DispatchCompute(tilesX, tilesY, 1u);
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit);
        if (!shade.Use()) { reason = "The OpenGL Advanced opaque shading program is not linked."; return false; }
        buffers.BindDispatchArguments();
        for (uint kernel = 0u; kernel < 128u; ++kernel)
        {
            buffers.BindPushConstants(view, kernel);
            RawGL.DispatchComputeIndirect((nint)(kernel * 16u));
        }
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit);
        buffers.BindPushConstants(view, 0u, overflowRepair: true);
        RawGL.DispatchCompute(tilesX, tilesY, 1u);
        // Authored background and late raster draws load this compute-written HDR image.
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit | MemoryBarrierMask.FramebufferBarrierBit);
        RawGL.BindBuffer(GLEnum.DispatchIndirectBuffer, 0u);
        reason = "Ready"; return true;
    }

    private static bool UnsupportedNativeStage(out string reason) { reason = "The requested OpenGL Advanced stage has no native compute executor."; return false; }

    private bool TryBindAdvancedNativeSamplers(uint firstUnit, Span<uint> previous, out string reason)
    {
        if (_advancedNativeSampler == 0u)
        {
            _advancedNativeSampler = RawGL.GenSampler();
            if (_advancedNativeSampler == 0u)
            {
                reason = "OpenGL could not allocate the Advanced native sampler.";
                return false;
            }
            RawGL.SamplerParameter(_advancedNativeSampler, GLEnum.TextureMinFilter, (int)GLEnum.Nearest);
            RawGL.SamplerParameter(_advancedNativeSampler, GLEnum.TextureMagFilter, (int)GLEnum.Nearest);
            RawGL.SamplerParameter(_advancedNativeSampler, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
            RawGL.SamplerParameter(_advancedNativeSampler, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);
            RawGL.SamplerParameter(_advancedNativeSampler, GLEnum.TextureWrapR, (int)GLEnum.ClampToEdge);
            RawGL.SamplerParameter(_advancedNativeSampler, GLEnum.TextureCompareMode, (int)GLEnum.None);
        }
        for (uint index = 0u; index < (uint)previous.Length; ++index)
        {
            previous[(int)index] = unchecked((uint)RawGL.GetInteger(GLEnum.SamplerBinding, firstUnit + index));
            RawGL.BindSampler(firstUnit + index, _advancedNativeSampler);
        }
        reason = "Ready";
        return true;
    }

    private void RestoreAdvancedNativeSamplers(uint firstUnit, ReadOnlySpan<uint> previous)
    {
        for (uint index = 0u; index < (uint)previous.Length; ++index)
            RawGL.BindSampler(firstUnit + index, previous[(int)index]);
    }

    private static uint DivideRoundUp(uint value, uint divisor) => Math.Max(1u, checked((value + divisor - 1u) / divisor));
}
