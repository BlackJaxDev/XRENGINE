using System.Collections.Generic;

using Silk.NET.Vulkan;

using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	/// <summary>
	/// Holds one draw's recording inputs and failure state without an allocation.
	/// The recording lock protects the renderer state that these inputs use.
	/// </summary>
	private struct DrawRecordingState
	{
		public CommandBuffer CommandBuffer;
		public PendingMeshDraw Draw;
		public XRFrameBuffer? Target;
		public VulkanWorldUiNativeProof? WorldUiProof;
		public VulkanAcceptedFramePlan? WorldUiPlan;
		public ulong WorldUiNativeToken;
		public bool WorldUiProofRequired;
		public XRMaterial Material;
		public RenderPass RenderPass;
		public bool UseDynamicRendering;
		public DynamicRenderingFormatSignature DynamicRenderingFormats;
		public int PassIndex;
		public IReadOnlyCollection<RenderPassMetadata>? PassMetadata;
		public bool DepthStencilReadOnly;
		public string PipelineName;
		public string TargetName;
		public int DrawUniformSlot;
		public int FrameDataImageIndex;
		public bool VerboseTrace;
		public bool UniformsNotified;
		public string? FailureReason;
		public bool PipelineFailureObserved;
	}
}
