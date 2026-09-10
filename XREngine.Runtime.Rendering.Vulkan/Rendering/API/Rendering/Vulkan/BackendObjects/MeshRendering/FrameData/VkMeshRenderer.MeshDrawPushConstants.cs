// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.MeshDrawPushConstants.cs  – partial class: Draw Command Recording
//
// Records indexed and non-indexed draw commands into Vulkan command buffers.
// Handles vertex buffer binding, descriptor set binding, and per-draw uniform
// notification.
// ──────────────────────────────────────────────────────────────────────────────

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	internal readonly struct MeshDrawPushConstants(uint materialIdentity, uint instanceCount, uint billboardMode, uint debugFlags) : IEquatable<MeshDrawPushConstants>
    {
		public readonly uint MaterialIdentity = materialIdentity;
		public readonly uint InstanceCount = instanceCount;
		public readonly uint BillboardMode = billboardMode;
		public readonly uint DebugFlags = debugFlags;

		public bool Equals(MeshDrawPushConstants other)
			=> MaterialIdentity == other.MaterialIdentity && InstanceCount == other.InstanceCount &&
				BillboardMode == other.BillboardMode && DebugFlags == other.DebugFlags;

		public override bool Equals(object? obj) => obj is MeshDrawPushConstants other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(MaterialIdentity, InstanceCount, BillboardMode, DebugFlags);
    }
}
