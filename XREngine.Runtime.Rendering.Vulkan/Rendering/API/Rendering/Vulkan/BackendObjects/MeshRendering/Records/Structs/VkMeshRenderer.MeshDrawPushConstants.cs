// ──────────────────────────────────────────────────────────────────────────────
// VulkanRenderer.VkMeshRenderer.MeshDrawPushConstants.cs  – partial class: Draw Command Recording
//
// Records indexed and non-indexed draw commands into Vulkan command buffers.
// Handles vertex buffer binding, descriptor set binding, and per-draw uniform
// notification.
// ──────────────────────────────────────────────────────────────────────────────

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
	public partial class VkMeshRenderer
	{
        internal readonly struct MeshDrawPushConstants(uint materialIdentity, uint instanceCount, uint billboardMode, uint debugFlags)
        {
			public readonly uint MaterialIdentity = materialIdentity;
			public readonly uint InstanceCount = instanceCount;
			public readonly uint BillboardMode = billboardMode;
			public readonly uint DebugFlags = debugFlags;
        }
	}
}
