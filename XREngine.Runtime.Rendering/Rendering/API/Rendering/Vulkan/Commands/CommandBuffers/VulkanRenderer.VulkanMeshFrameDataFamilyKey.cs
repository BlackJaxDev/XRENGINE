using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct VulkanMeshFrameDataFamilyKey(
        int FrameDataSlot,
        EVulkanMeshFrameDataStreamKind StreamKind,
        EVulkanFrameOpContextKind ContextKind,
        int PipelineIdentity,
        int ViewportIdentity,
        int OutputFrameBufferIdentity,
        int OutputTargetIdentity,
        int CameraIdentity,
        int StereoRightEyeCameraIdentity,
        bool StereoEnabled,
        bool MultiviewEnabled)
    {
        public static VulkanMeshFrameDataFamilyKey From(
            int frameDataSlot,
            EVulkanMeshFrameDataStreamKind streamKind,
            in FrameOpContext context,
            in PendingMeshDraw draw)
            => new(
                frameDataSlot,
                streamKind,
                context.ContextKind,
                context.PipelineIdentity,
                context.ViewportIdentity,
                context.OutputFrameBufferIdentity,
                context.OutputTargetIdentity,
                draw.Camera is null ? 0 : RuntimeHelpers.GetHashCode(draw.Camera),
                draw.StereoRightEyeCamera is null ? 0 : RuntimeHelpers.GetHashCode(draw.StereoRightEyeCamera),
                context.StereoEnabled,
                context.MultiviewEnabled);
    }
}
