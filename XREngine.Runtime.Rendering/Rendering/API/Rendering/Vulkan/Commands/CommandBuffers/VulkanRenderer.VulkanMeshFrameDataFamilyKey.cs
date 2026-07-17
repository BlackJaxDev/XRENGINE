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

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = FrameDataSlot;
                hash = (hash * 397) ^ (int)StreamKind;
                hash = (hash * 397) ^ (int)ContextKind;
                hash = (hash * 397) ^ PipelineIdentity;
                hash = (hash * 397) ^ ViewportIdentity;
                hash = (hash * 397) ^ OutputFrameBufferIdentity;
                hash = (hash * 397) ^ OutputTargetIdentity;
                hash = (hash * 397) ^ CameraIdentity;
                hash = (hash * 397) ^ StereoRightEyeCameraIdentity;
                hash = (hash * 397) ^ (StereoEnabled ? 1 : 0);
                return (hash * 397) ^ (MultiviewEnabled ? 1 : 0);
            }
        }
    }
}
