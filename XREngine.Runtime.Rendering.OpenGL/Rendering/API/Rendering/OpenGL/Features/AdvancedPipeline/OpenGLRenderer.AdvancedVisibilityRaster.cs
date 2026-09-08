using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.OVR;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private uint _advancedRasterFramebuffer;

    /// <summary>Renders indexed streams from one immutable scene atlas, using
    /// one hardware multiview submission stream for a stereo family.</summary>
    private unsafe bool TryRasterAdvancedVisibility(in AdvancedVisibilityStageBackendRequest request, bool late, out string reason)
    {
        if (!TryCaptureAdvancedVisibilityOutputClosure(in request, out var target, out reason) ||
            _advancedInputStorage is null || _advancedOutputRegistry is null ||
            !_advancedOutputRegistry.TryGetRetainedSlot(request.Reservation, out var slot) || slot is null)
            return false;
        ReadOnlySpan<AdvancedIndirectRange> ranges = _advancedInputStorage.IndirectRanges;
        for (int index = 0; index < ranges.Length; index++)
            if (ranges[index].Key.Producer is not (EAdvancedGeometryProducer.IndirectIndexed or
                    EAdvancedGeometryProducer.CpuDirectStaticIndexed or EAdvancedGeometryProducer.CpuDirectPreSkinned) ||
                ranges[index].Key.Coverage is not (EAdvancedMaterialCoverageMode.Opaque or EAdvancedMaterialCoverageMode.Masked))
            {
                reason = "OpenGL Advanced raster requires a selected indexed producer and opaque/masked coverage.";
                return false;
            }
        if (_advancedSceneUploader is null || _advancedAtlasVao is null)
        {
            reason = "OpenGL Advanced visibility has no prepared geometry atlas.";
            return false;
        }
        bool stereo = request.Views.ViewCount == 2;
        if (stereo && (!TryEnsureAdvancedStereoPrograms(out reason) ||
                       !TryBuildAdvancedStereoRasterStream(slot, late, out reason)))
            return false;
        if (stereo && (target.LayerCount != 2u || request.Views.GetView(0).ReversedDepth != request.Views.GetView(1).ReversedDepth))
        {
            reason = "OpenGL Advanced multiview raster requires two layers with the same depth convention.";
            return false;
        }

        int oldFramebuffer = RawGL.GetInteger(GLEnum.DrawFramebufferBinding);
        int oldVertexArray = RawGL.GetInteger(GLEnum.VertexArrayBinding);
        int oldIndirectBuffer = RawGL.GetInteger(GLEnum.DrawIndirectBufferBinding);
        int oldParameterBuffer = RawGL.GetInteger(GLEnum.ParameterBufferBinding);
        int oldDepthFunc = RawGL.GetInteger(GLEnum.DepthFunc);
        int oldCull = RawGL.GetInteger(GLEnum.CullFaceMode);
        int oldFront = RawGL.GetInteger(GLEnum.FrontFace);
        int* oldViewport = stackalloc int[4];
        RawGL.GetInteger(GLEnum.Viewport, oldViewport);
        int* oldColorWrites = stackalloc int[12];
        for (uint attachment = 0; attachment < 3; ++attachment)
            RawGL.GetInteger(GLEnum.ColorWritemask, attachment, oldColorWrites + attachment * 4);
        bool oldDepthTest = RawGL.IsEnabled(GLEnum.DepthTest);
        bool oldBlend = RawGL.IsEnabled(GLEnum.Blend);
        bool oldCullEnabled = RawGL.IsEnabled(GLEnum.CullFace);
        bool oldScissor = RawGL.IsEnabled(GLEnum.ScissorTest);
        bool oldStencil = RawGL.IsEnabled(GLEnum.StencilTest);
        bool oldDepthWrite = RawGL.GetBoolean(GLEnum.DepthWritemask);
        if (_advancedRasterFramebuffer == 0) _advancedRasterFramebuffer = RawGL.GenFramebuffer();
        try
        {
            if (!_advancedAtlasVao.TryBind(_advancedSceneUploader.IndexBuffer, out reason))
                return false;
            RawGL.BindFramebuffer(GLEnum.DrawFramebuffer, _advancedRasterFramebuffer);
            RawGL.Enable(GLEnum.DepthTest);
            RawGL.Disable(GLEnum.Blend);
            RawGL.Disable(GLEnum.ScissorTest);
            RawGL.Disable(GLEnum.StencilTest);
            RawGL.DepthMask(true);
            // Native identity/metadata/selection writes cannot inherit a preceding
            // depth-only or editor pass's color mask, including their clears.
            for (uint attachment = 0; attachment < 3; ++attachment)
                RawGL.ColorMask(attachment, true, true, true, true);
            RawGL.FrontFace(FrontFaceDirection.Ccw);
            RawGL.Viewport(0, 0, target.Width, target.Height);
            uint* attachments = stackalloc uint[3] { (uint)GLEnum.ColorAttachment0, (uint)GLEnum.ColorAttachment1, (uint)GLEnum.ColorAttachment2 };
            RawGL.DrawBuffers(3, (GLEnum*)attachments);
            RawGL.BindBuffer(GLEnum.DrawIndirectBuffer, slot.Buffer(stereo ? 75u : late ? 70u : 58u));
            RawGL.BindBuffer(GLEnum.ParameterBuffer, slot.Buffer(stereo ? 76u : late ? 69u : 56u));
            Span<uint> push = stackalloc uint[4];
            uint* empty = stackalloc uint[4] { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue };
            uint payloadCount = checked((uint)_advancedInputStorage.Payloads.Length);
            for (uint view = 0; view < (stereo ? 1 : request.Views.ViewCount); view++)
            {
                if (stereo)
                {
                    // One framebuffer and submission stream broadcasts to both
                    // eyes. The vertex shader applies the independently built mask.
                    OVRMultiView!.NamedFramebufferTextureMultiview(_advancedRasterFramebuffer, (OVR)GLEnum.ColorAttachment0, target.IdentityId, 0, 0, 2u);
                    OVRMultiView.NamedFramebufferTextureMultiview(_advancedRasterFramebuffer, (OVR)GLEnum.ColorAttachment1, target.MetadataId, 0, 0, 2u);
                    OVRMultiView.NamedFramebufferTextureMultiview(_advancedRasterFramebuffer, (OVR)GLEnum.ColorAttachment2, target.SelectionId, 0, 0, 2u);
                    OVRMultiView.NamedFramebufferTextureMultiview(_advancedRasterFramebuffer, (OVR)GLEnum.DepthAttachment, target.DepthId, 0, 0, 2u);
                }
                else
                {
                    RawGL.NamedFramebufferTextureLayer(_advancedRasterFramebuffer, GLEnum.ColorAttachment0, target.IdentityId, 0, (int)view);
                    RawGL.NamedFramebufferTextureLayer(_advancedRasterFramebuffer, GLEnum.ColorAttachment1, target.MetadataId, 0, (int)view);
                    RawGL.NamedFramebufferTextureLayer(_advancedRasterFramebuffer, GLEnum.ColorAttachment2, target.SelectionId, 0, (int)view);
                    RawGL.NamedFramebufferTextureLayer(_advancedRasterFramebuffer, GLEnum.DepthAttachment, target.DepthId, 0, (int)view);
                }
                if (RawGL.CheckNamedFramebufferStatus(_advancedRasterFramebuffer, GLEnum.DrawFramebuffer) != GLEnum.FramebufferComplete)
                {
                    reason = "OpenGL Advanced visibility eye attachments are incomplete.";
                    return false;
                }
                bool reversed = request.Views.GetView((int)view).ReversedDepth;
                RawGL.DepthFunc(reversed ? DepthFunction.Gequal : DepthFunction.Lequal);
                if (!late)
                {
                    RawGL.ClearNamedFramebuffer(_advancedRasterFramebuffer, GLEnum.Color, 0, empty);
                    RawGL.ClearNamedFramebuffer(_advancedRasterFramebuffer, GLEnum.Color, 1, empty);
                    RawGL.ClearNamedFramebuffer(_advancedRasterFramebuffer, GLEnum.Color, 2, empty);
                    float clearDepth = reversed ? 0f : 1f;
                    RawGL.ClearNamedFramebuffer(_advancedRasterFramebuffer, GLEnum.Depth, 0, &clearDepth);
                }
                for (int index = 0; index < ranges.Length; index++)
                {
                    ref readonly AdvancedIndirectRange range = ref ranges[index];
                    bool direct = range.Key.Producer is EAdvancedGeometryProducer.CpuDirectStaticIndexed or
                        EAdvancedGeometryProducer.CpuDirectPreSkinned;
                    // Direct submission was selected by the canonical strategy resolver.
                    // Its complete stream is emitted once; late recovery is GPU-only.
                    if (late && direct)
                        continue;
                    GLRenderProgram? program = GenericToAPI<GLRenderProgram>(range.Key.Coverage == EAdvancedMaterialCoverageMode.Masked
                        ? (stereo ? _advancedStereoMaskedRasterProgram : _advancedVisibilityMaskedRasterProgram)
                        : (stereo ? _advancedStereoRasterProgram : _advancedVisibilityRasterProgram));
                    if (program is null || !program.Use())
                    {
                        reason = "OpenGL Advanced visibility raster program is unavailable.";
                        return false;
                    }
                    if (range.Key.CullMode == (uint)ECullMode.None)
                        RawGL.Disable(GLEnum.CullFace);
                    else
                    {
                        RawGL.Enable(GLEnum.CullFace);
                        RawGL.CullFace((TriangleFace)ToGLEnum((ECullMode)range.Key.CullMode));
                    }
                    push[0] = range.FirstPayloadIndex;
                    push[1] = (uint)range.Key.Producer | (late ? 8u : 0u);
                    push[2] = view;
                    push[3] = 1u;
                    slot.UploadUniform(this, 2u, push);
                    if (direct)
                    {
                        uint end = checked(range.FirstPayloadIndex + range.PayloadCapacity);
                        for (uint ordered = range.FirstPayloadIndex; ordered < end; ordered++)
                        {
                            int payloadIndex = _advancedInputStorage.IndirectPayloadIndices[(int)ordered];
                            ref readonly AdvancedVisibilityPayload payload = ref _advancedInputStorage.Payloads[payloadIndex];
                            RawGL.DrawElementsInstancedBaseVertexBaseInstance(GLEnum.Triangles, payload.IndexCount,
                                GLEnum.UnsignedInt, (void*)checked((nuint)payload.FirstIndex * sizeof(uint)),
                                Math.Max(1u, payload.InstanceCount), checked((int)payload.GeometryOffsets.VertexOffset),
                                checked((uint)payloadIndex));
                        }
                        continue;
                    }
                    nuint argumentOffset = checked((nuint)(view * payloadCount + range.FirstPayloadIndex) * 20u);
                    nint countOffset = checked((nint)(view * (uint)ranges.Length + (uint)index) * 4);
                    RawGL.MultiDrawElementsIndirectCount(GLEnum.Triangles, GLEnum.UnsignedInt,
                        (void*)argumentOffset, countOffset, range.PayloadCapacity, 20u);
                }
            }
            RawGL.MemoryBarrier(MemoryBarrierMask.FramebufferBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit | MemoryBarrierMask.ShaderStorageBarrierBit);
            reason = "Ready";
            return true;
        }
        finally
        {
            RawGL.BindVertexArray((uint)oldVertexArray);
            RawGL.BindBuffer(GLEnum.DrawIndirectBuffer, (uint)oldIndirectBuffer);
            RawGL.BindBuffer(GLEnum.ParameterBuffer, (uint)oldParameterBuffer);
            RawGL.BindFramebuffer(GLEnum.DrawFramebuffer, (uint)oldFramebuffer);
            RawGL.Viewport(oldViewport[0], oldViewport[1], (uint)oldViewport[2], (uint)oldViewport[3]);
            RawGL.DepthFunc((DepthFunction)oldDepthFunc);
            RawGL.DepthMask(oldDepthWrite);
            for (uint attachment = 0; attachment < 3; ++attachment)
            {
                int* write = oldColorWrites + attachment * 4;
                RawGL.ColorMask(attachment, write[0] != 0, write[1] != 0, write[2] != 0, write[3] != 0);
            }
            RawGL.CullFace((TriangleFace)oldCull);
            RawGL.FrontFace((FrontFaceDirection)oldFront);
            RestoreAdvancedEnable(GLEnum.DepthTest, oldDepthTest);
            RestoreAdvancedEnable(GLEnum.Blend, oldBlend);
            RestoreAdvancedEnable(GLEnum.CullFace, oldCullEnabled);
            RestoreAdvancedEnable(GLEnum.ScissorTest, oldScissor);
            RestoreAdvancedEnable(GLEnum.StencilTest, oldStencil);
        }
    }

    private void RestoreAdvancedEnable(GLEnum capability, bool enabled)
    {
        if (enabled) RawGL.Enable(capability);
        else RawGL.Disable(capability);
    }

    private bool TryDispatchAdvancedLateVisibility(in AdvancedVisibilityStageBackendRequest request, out string reason)
    {
        if (!TryCaptureAdvancedVisibilityOutputClosure(in request, out var target, out reason) ||
            _advancedOutputRegistry is null || !_advancedOutputRegistry.TryGetRetainedSlot(request.Reservation, out var slot) ||
            slot is null || _advancedInputStorage is null)
            return false;
        GLRenderProgram? pyramid = GenericToAPI<GLRenderProgram>(_advancedDepthPyramidProgram);
        GLRenderProgram? late = GenericToAPI<GLRenderProgram>(_advancedLateVisibilityProgram);
        if (pyramid is null || late is null) { reason = "OpenGL Advanced late programs are unavailable."; return false; }
        Span<uint> priorSampler = stackalloc uint[1];
        if (!TryBindAdvancedNativeSamplers(4u, priorSampler, out reason))
            return false;
        try
        {
        Span<uint> push = stackalloc uint[4];
        uint count = checked((uint)_advancedInputStorage.Payloads.Length);
        for (uint view = 0; view < request.Views.ViewCount; view++)
        {
            push[0] = view; push[1] = view * count; push[2] = count;
            push[3] = view * checked((uint)_advancedInputStorage.IndirectRanges.Length);
            slot.UploadUniform(this, 0u, push);
            RawGL.BindTextureUnit(4u, target.DepthId);
            RawGL.BindImageTexture(5u, target.DepthPyramidId, 0, true, 0, BufferAccessARB.WriteOnly, InternalFormat.R32f);
            if (!pyramid.Use()) { reason = "OpenGL depth reduction program did not link."; return false; }
            RawGL.DispatchCompute((target.Width + 63u) / 64u, (target.Height + 63u) / 64u, 1u);
            RawGL.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);
            RawGL.BindTextureUnit(4u, target.DepthPyramidId);
            if (!late.Use()) { reason = "OpenGL late visibility program did not link."; return false; }
            RawGL.DispatchCompute(Math.Max(1u, (count + 255u) / 256u), 1u, 1u);
            RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.CommandBarrierBit);
        }
        reason = "Ready";
        return true;
        }
        finally { RestoreAdvancedNativeSamplers(4u, priorSampler); }
    }
}
