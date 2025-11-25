using System.Numerics;
using XREngine;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
	/// <summary>
	/// Minimal placeholder that keeps the Vulkan backend compiling while mesh rendering is under construction.
	/// </summary>
	public class VkMeshRenderer(VulkanRenderer api, XRMeshRenderer.BaseVersion data) : VkObject<XRMeshRenderer.BaseVersion>(api, data)
	{
		public override VkObjectType Type => VkObjectType.MeshRenderer;
		public override bool IsGenerated => true;

		protected override uint CreateObjectInternal() => CacheObject(this);
		protected override void DeleteObjectInternal() => RemoveCachedObject(BindingId);

		protected override void LinkData()
			=> Data.RenderRequested += OnRenderRequested;

		protected override void UnlinkData()
			=> Data.RenderRequested -= OnRenderRequested;

		private void OnRenderRequested(Matrix4x4 modelMatrix, Matrix4x4 prevModelMatrix, XRMaterial? materialOverride, uint instances, EMeshBillboardMode billboardMode)
			=> Debug.LogWarning("Vulkan mesh rendering is not implemented yet; ignoring render request.");
	}
}