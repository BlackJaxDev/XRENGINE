// ──────────────────────────────────────────────────────────────────────────────
// VulkanRenderer.VkMeshRenderer.BufferStructuralIdentity.cs  – partial class: Buffer & Material Resolution
//
// Gathers GPU data buffers from mesh/renderer, resolves index buffers, and
// determines the effective material for each draw call.
// ──────────────────────────────────────────────────────────────────────────────

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
	public partial class VkMeshRenderer
	{
        private readonly record struct BufferStructuralIdentity(
			ulong Handle,
			ulong AllocationGeneration,
			ulong Range,
			uint Binding,
			EBufferTarget Target,
			EComponentType ComponentType,
			uint ComponentCount,
			uint ElementCount);
	}
}
