namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record MeshDrawOp(int PassIndex, XRFrameBuffer? Target, PendingMeshDraw Draw, FrameOpContext Context) : FrameOp(PassIndex, Target, Context)
    {
        /// <summary>
        /// True when this draw was enqueued inside an occlusion QueryOp Begin/End bracket
        /// (CPU occlusion proxy AABB draws). Such draws must keep their enqueue position
        /// relative to the surrounding QueryOps: canonical opaque-draw reordering would
        /// make the frame-op sort comparer intransitive and scramble Begin/End pairing
        /// (observed as VUID-vkCmdBeginQuery-queryPool-01922 and
        /// VUID-vkEndCommandBuffer-commandBuffer-00061).
        /// </summary>
        internal bool PreserveSubmissionOrder { get; init; }
    }
}