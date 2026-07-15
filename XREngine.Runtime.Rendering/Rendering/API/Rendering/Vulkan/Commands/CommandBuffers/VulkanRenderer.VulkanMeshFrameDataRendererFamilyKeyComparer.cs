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
            => ReferenceEquals(x.Renderer, y.Renderer) && x.Family == y.Family;

        public int GetHashCode(VulkanMeshFrameDataRendererFamilyKey obj)
            => HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Renderer), obj.Family);
    }
}
