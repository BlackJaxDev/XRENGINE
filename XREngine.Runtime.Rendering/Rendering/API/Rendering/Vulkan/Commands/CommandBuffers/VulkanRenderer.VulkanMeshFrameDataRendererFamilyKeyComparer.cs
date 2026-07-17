using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed class VulkanMeshFrameDataRendererFamilyKeyComparer :
        IEqualityComparer<VulkanMeshFrameDataRendererFamilyKey>
    {
        public static VulkanMeshFrameDataRendererFamilyKeyComparer Instance { get; } = new();

        public bool Equals(
            VulkanMeshFrameDataRendererFamilyKey x,
            VulkanMeshFrameDataRendererFamilyKey y)
            => ReferenceEquals(x.Renderer, y.Renderer) &&
               x.Family.FrameDataSlot == y.Family.FrameDataSlot &&
               x.Family.StreamKind == y.Family.StreamKind &&
               x.Family.ContextKind == y.Family.ContextKind &&
               x.Family.PipelineIdentity == y.Family.PipelineIdentity &&
               x.Family.ViewportIdentity == y.Family.ViewportIdentity &&
               x.Family.OutputFrameBufferIdentity == y.Family.OutputFrameBufferIdentity &&
               x.Family.OutputTargetIdentity == y.Family.OutputTargetIdentity &&
               x.Family.CameraIdentity == y.Family.CameraIdentity &&
               x.Family.StereoRightEyeCameraIdentity == y.Family.StereoRightEyeCameraIdentity &&
               x.Family.StereoEnabled == y.Family.StereoEnabled &&
               x.Family.MultiviewEnabled == y.Family.MultiviewEnabled;

        public int GetHashCode(VulkanMeshFrameDataRendererFamilyKey obj)
            => unchecked((RuntimeHelpers.GetHashCode(obj.Renderer) * 397) ^ obj.Family.GetHashCode());
    }
}
