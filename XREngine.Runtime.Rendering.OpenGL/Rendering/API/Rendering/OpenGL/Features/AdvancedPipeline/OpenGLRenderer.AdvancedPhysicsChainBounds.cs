using Silk.NET.OpenGL;
using XREngine.Rendering.Compute;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private bool TryPatchAdvancedPhysicsChainBounds(
        OpenGLAdvancedVisibilityInputStorage inputs,
        OpenGLAdvancedVisibilitySlot slot,
        out string reason)
    {
        PhysicsChainGpuOutputPageLease page = inputs.BoundsPage;
        if (!page.Token.IsValid)
        {
            reason = "Ready";
            return true;
        }

        GLRenderProgram? patch = GenericToAPI<GLRenderProgram>(_advancedPhysicsChainBoundsProgram);
        GLDataBuffer? bounds = GenericToAPI<GLDataBuffer>(page.BoundsAtlasBuffer);
        GLDataBuffer? metadata = GenericToAPI<GLDataBuffer>(page.SlotMetadataBuffer);
        if (patch is null || !patch.Use() || bounds is null || metadata is null ||
            !bounds.TryGetBindingId(out uint boundsId) || !metadata.TryGetBindingId(out uint metadataId) ||
            !bounds.IsReadyForRendering || !metadata.IsReadyForRendering)
        {
            reason = "The OpenGL physics-chain bound page or patch program is not ready.";
            return false;
        }

        if (!slot.TryRetainPhysicsChainBoundsPage(page.Token))
        {
            reason = "The OpenGL physics-chain bound page changed before visibility submission.";
            return false;
        }

        slot.UploadPhysicsChainBoundsRoutes(this, inputs.BoundsRoutes);
        RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, 54u, boundsId);
        RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, 55u, metadataId);
        Span<uint> parameters = stackalloc uint[4]
        {
            checked((uint)inputs.Payloads.Length), page.ProducerEpoch, page.PageGeneration, 0u
        };
        slot.UploadUniform(this, 0u, parameters);
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.BufferUpdateBarrierBit);
        RawGL.DispatchCompute(Math.Max(1u, (parameters[0] + 255u) / 256u), 1u, 1u);
        slot.FencePhysicsChainBoundsRead(this);
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        for (uint binding = 53u; binding <= 55u; ++binding)
            RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, binding, slot.Buffer(binding));
        reason = "Ready";
        return true;
    }
}
