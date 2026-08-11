// Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
// VkMeshRenderer.Drawing.cs  Ã¢â‚¬â€œ partial class: Draw Command Recording
//
// Records indexed and non-indexed draw commands into Vulkan command buffers.
// Handles vertex buffer binding, descriptor set binding, and per-draw uniform
// notification.
// Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬

using System;
using System.Buffers;
using System.Numerics;
using System.Threading;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	private readonly Lock _recordDrawSync = new();

	private static IDisposable? StartMeshDrawDetailScope(string name)
		=> VulkanMeshRenderingConventions.CommandRecordingDetailProfilingEnabled
			? RuntimeRenderingHostServices.Profiling.StartProfileScope(name)
			: null;

	/// <summary>
	/// Records draw commands into the given Vulkan command buffer for all
	/// primitive types available on the mesh (triangles, lines, points).
	/// Falls back to non-indexed drawing if no index buffers are present.
	/// Handles pipeline binding, vertex buffer binding, skinning/blendshape
	/// data upload, and descriptor set binding.
	/// </summary>
	internal bool RecordDraw(
		CommandBuffer commandBuffer,
		in PendingMeshDraw draw,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		bool depthStencilReadOnly,
		string pipelineName,
		string targetName,
		int drawUniformSlot,
		int frameDataImageIndex)
	{
		lock (_recordDrawSync)
		{
			return RecordDrawNoLock(
				commandBuffer,
				draw,
				renderPass,
				useDynamicRendering,
				dynamicRenderingFormats,
				passIndex,
				passMetadata,
				depthStencilReadOnly,
				pipelineName,
				targetName,
				drawUniformSlot,
				frameDataImageIndex);
		}
	}

	private bool RecordDrawNoLock(
		CommandBuffer commandBuffer,
		in PendingMeshDraw draw,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		bool depthStencilReadOnly,
		string pipelineName,
		string targetName,
		int drawUniformSlot,
		int frameDataImageIndex)
	{
		var material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
		string prepareReason;
		bool preparedForRecord;
		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.Prepare"))
		{
			preparedForRecord = draw.PreparedProgram is { } preparedProgram
				? TryPrepareCapturedProgramForRecording(material, preparedProgram, draw.PreparedProgramIdentity, draw.PreparedProgramLinkGeneration, draw.ProgramBindingSnapshot, drawUniformSlot, out prepareReason)
				: TryPrepareForRendering(material, out prepareReason);
		}
		if (!preparedForRecord)
		{
			if (XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw)
				Debug.MeshesWarning("[DrawTrace] {0}: skipped before command recording because preparation failed: {1} {2}", Mesh?.Name ?? "?", prepareReason, LastPrepareDetail);
			return false;
		}

		var drawCopy = draw; // struct copy required for capture in local function closures
		uint drawInstances = draw.Instances;
		bool skipLinePointDraws = MeshRenderMaterialResolver.RequiresTriangleOnlyDrawsForCurrentPass();

		// Trace swapchain (dynamic rendering) draws only.
		bool traceSwapchain = useDynamicRendering && renderPass.Handle == 0;
		bool verboseSwapchainTrace = traceSwapchain && XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw;

		// Trace ALL draws (including FBO-targeted UI batched draws) for debugging.
		bool verboseAllDrawTrace = XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw;
		bool verboseTrace = verboseSwapchainTrace || verboseAllDrawTrace;

		// Skinning and blendshape weights must be pushed before draw commands
		// reference them; static meshes avoid the shared invalidation checks.
		if (Mesh?.HasSkinning == true)
		{
			using (StartMeshDrawDetailScope("Vulkan.MeshDraw.PushSkinning"))
				MeshRenderer.PushBoneMatricesToGPU();
		}
		if (Mesh?.HasBlendshapes == true)
		{
			using (StartMeshDrawDetailScope("Vulkan.MeshDraw.PushBlendshapes"))
				MeshRenderer.PushBlendshapeWeightsToGPU();
		}

		bool uniformsNotified = false;

		bool DrawIndexed(VkDataBuffer? indexBuffer, IndexSize size, PrimitiveTopology topology)
		{
			if (indexBuffer?.BufferHandle is not { } indexHandle)
			{
				if (verboseTrace)
					Debug.MeshesWarning("[DrawTrace] {0}: no indexBuffer for {1}", Mesh?.Name ?? "?", topology);
				return false;
			}

			if (size == IndexSize.Byte && !BackendContext.Supports(EVulkanDeviceCapability.IndexTypeUint8))
			{
				WarnOnce("Skipping indexed draw using byte-sized indices because Vulkan indexTypeUint8 is not enabled.");
				return false;
			}

			uint indexCount = indexBuffer.Data.ElementCount;
			if (indexCount == 0)
			{
				if (verboseTrace)
					Debug.MeshesWarning("[DrawTrace] {0}: indexCount=0 for {1}", Mesh?.Name ?? "?", topology);
				return false;
			}

			Pipeline pipeline = default;
			bool pipelineReady;
			using (StartMeshDrawDetailScope("Vulkan.MeshDraw.EnsurePipeline"))
			{
				pipelineReady = EnsurePipeline(material, topology, drawCopy, renderPass, useDynamicRendering, dynamicRenderingFormats, passIndex, passMetadata, depthStencilReadOnly, pipelineName, allowPipelineCreation: false, out pipeline);
			}
			if (!pipelineReady)
			{
				if (verboseTrace)
					Debug.MeshesWarning("[DrawTrace] {0}: EnsurePipeline FAILED for {1} dynRender={2} colors={3} depthFmt={4}",
						Mesh?.Name ?? "?", topology, useDynamicRendering, dynamicRenderingFormats.DescribeColorFormats(), dynamicRenderingFormats.DepthAttachmentFormat);
				return false;
			}

			using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindPipeline"))
				CommandOperations.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

			bool vertexBuffersBound;
			using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindVertexBuffers"))
				vertexBuffersBound = BindVertexBuffersForCurrentPipeline(commandBuffer);
			if (!vertexBuffersBound)
			{
				if (verboseTrace)
					Debug.MeshesWarning("[DrawTrace] {0}: BindVertexBuffers FAILED", Mesh?.Name ?? "?");
				return false;
			}

			if (!uniformsNotified && _program?.Data is { } programData)
			{
				using (StartMeshDrawDetailScope("Vulkan.MeshDraw.NotifyUniforms"))
					NotifyDrawUniforms(material, programData, drawCopy);
				uniformsNotified = true;
			}

			using (StartMeshDrawDetailScope("Vulkan.MeshDraw.PushConstants"))
				PushPerDrawConstants(commandBuffer, material, drawCopy);

			bool descriptorsBound;
			using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindDescriptors"))
				descriptorsBound = BindDescriptorsIfAvailable(commandBuffer, material, drawCopy, drawUniformSlot, frameDataImageIndex, passIndex);
			if (!descriptorsBound)
			{
				if (verboseTrace)
					Debug.MeshesWarning("[DrawTrace] {0}: BindDescriptors FAILED", Mesh?.Name ?? "?");
				return false;
			}

			if (verboseTrace)
				Debug.MeshesWarning("[DrawTrace] {0}: CmdDrawIndexed({1}) pass={2} target={3} dynRender={4} dsReadOnly={5} pipeline=0x{6:X} topology={7} cull={8} blend={9} blendOps={10}/{11} blendFactors={12},{13},{14},{15} alphaToCoverage={16} depthTest={17} depthWrite={18} depthCmp={19} colorWrite={20} viewport=({21},{22},{23},{24}) scissor=({25},{26},{27},{28}) prog={29}",
					Mesh?.Name ?? "?", indexCount,
					passIndex, targetName, useDynamicRendering, depthStencilReadOnly,
					pipeline.Handle, topology,
					drawCopy.CullMode, drawCopy.BlendEnabled,
					drawCopy.ColorBlendOp, drawCopy.AlphaBlendOp,
					drawCopy.SrcColorBlendFactor, drawCopy.DstColorBlendFactor, drawCopy.SrcAlphaBlendFactor, drawCopy.DstAlphaBlendFactor,
					drawCopy.AlphaToCoverageEnabled,
					drawCopy.DepthTestEnabled, drawCopy.DepthWriteEnabled, drawCopy.DepthCompareOp, drawCopy.ColorWriteMask,
					drawCopy.Viewport.X, drawCopy.Viewport.Y, drawCopy.Viewport.Width, drawCopy.Viewport.Height,
					drawCopy.Scissor.Offset.X, drawCopy.Scissor.Offset.Y, drawCopy.Scissor.Extent.Width, drawCopy.Scissor.Extent.Height,
					_program?.Data?.Name ?? "?prog");

			using (StartMeshDrawDetailScope("Vulkan.MeshDraw.CmdDrawIndexed"))
			{
				CommandOperations.BindIndexBufferTracked(commandBuffer, indexHandle, 0, ToVkIndexType(size));
				Api!.CmdDrawIndexed(commandBuffer, indexCount, drawInstances, 0, 0, 0);
			}

			return true;
		}

		// Attempt indexed draws for each primitive type in priority order.
		// The first successful draw sets 'drew = true' so we skip the non-indexed fallback.
		bool drew = false;
		if (_triangleIndexBuffer?.BufferHandle is { } triHandle && triHandle.Handle != 0)
			drew |= DrawIndexed(_triangleIndexBuffer, _triangleIndexSize, PrimitiveTopology.TriangleList);
		if (!skipLinePointDraws)
		{
			if (_lineIndexBuffer?.BufferHandle is { } lineHandle && lineHandle.Handle != 0)
				drew |= DrawIndexed(_lineIndexBuffer, _lineIndexSize, PrimitiveTopology.LineList);
			if (_pointIndexBuffer?.BufferHandle is { } pointHandle && pointHandle.Handle != 0)
				drew |= DrawIndexed(_pointIndexBuffer, _pointIndexSize, PrimitiveTopology.PointList);
		}
		else if ((_lineIndexBuffer?.BufferHandle?.Handle ?? 0UL) != 0UL || (_pointIndexBuffer?.BufferHandle?.Handle ?? 0UL) != 0UL)
		{
			Debug.VulkanWarningEvery(
				$"Vulkan.MeshRenderer.ShadowTriangleOnly.{Mesh?.Name ?? "UnnamedMesh"}",
				TimeSpan.FromSeconds(2),
				"[Vulkan] Suppressed line/point index draws for shadow geometry pass. mesh='{0}' layout={1}",
				Mesh?.Name ?? "<unnamed mesh>",
				_geometryLayoutSignature.DebugSummary);
		}

		if (!drew && Mesh is not null)
		{
			uint vertexCount = (uint)Math.Max(Mesh.VertexCount, 0);
			PrimitiveTopology fallbackTopology = Mesh.Type switch
			{
				EPrimitiveType.Points => PrimitiveTopology.PointList,
				EPrimitiveType.Lines => PrimitiveTopology.LineList,
				EPrimitiveType.LineStrip => PrimitiveTopology.LineStrip,
				EPrimitiveType.TriangleStrip => PrimitiveTopology.TriangleStrip,
				EPrimitiveType.TriangleFan => PrimitiveTopology.TriangleFan,
				EPrimitiveType.Patches => PrimitiveTopology.PatchList,
				_ => PrimitiveTopology.TriangleList,
			};

			if (skipLinePointDraws && !IsTriangleClassTopology(fallbackTopology))
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.MeshRenderer.ShadowTriangleOnlyFallback.{Mesh?.Name ?? "UnnamedMesh"}",
					TimeSpan.FromSeconds(2),
					"[Vulkan] Suppressed non-indexed {0} fallback for shadow geometry pass. mesh='{1}' layout={2}",
					fallbackTopology,
					Mesh?.Name ?? "<unnamed mesh>",
					_geometryLayoutSignature.DebugSummary);
				return false;
			}

			Pipeline pipeline = default;
			bool pipelineReady;
			using (StartMeshDrawDetailScope("Vulkan.MeshDraw.EnsurePipeline"))
			{
				pipelineReady = vertexCount > 0 &&
					EnsurePipeline(material, fallbackTopology, drawCopy, renderPass, useDynamicRendering, dynamicRenderingFormats, passIndex, passMetadata, depthStencilReadOnly, pipelineName, allowPipelineCreation: false, out pipeline);
			}
			if (pipelineReady)
			{
				using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindPipeline"))
					CommandOperations.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

				bool vertexBuffersBound;
				using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindVertexBuffers"))
					vertexBuffersBound = BindVertexBuffersForCurrentPipeline(commandBuffer);
				if (!vertexBuffersBound)
					return false;

				if (!uniformsNotified && _program?.Data is { } programData)
				{
					using (StartMeshDrawDetailScope("Vulkan.MeshDraw.NotifyUniforms"))
						NotifyDrawUniforms(material, programData, drawCopy);
					uniformsNotified = true;
				}

				using (StartMeshDrawDetailScope("Vulkan.MeshDraw.PushConstants"))
					PushPerDrawConstants(commandBuffer, material, drawCopy);

				bool descriptorsBound;
				using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindDescriptors"))
					descriptorsBound = BindDescriptorsIfAvailable(commandBuffer, material, drawCopy, drawUniformSlot, frameDataImageIndex, passIndex);
				if (!descriptorsBound)
					return false;

				if (verboseTrace)
					Debug.MeshesWarning("[DrawTrace] {0}: CmdDraw({1}) pass={2} target={3} dynRender={4} dsReadOnly={5} pipeline=0x{6:X} topology={7} cull={8} blend={9} blendOps={10}/{11} blendFactors={12},{13},{14},{15} alphaToCoverage={16} depthTest={17} depthWrite={18} depthCmp={19} colorWrite={20} viewport=({21},{22},{23},{24}) scissor=({25},{26},{27},{28}) prog={29}",
						Mesh?.Name ?? "?", vertexCount,
						passIndex, targetName, useDynamicRendering, depthStencilReadOnly,
						pipeline.Handle, fallbackTopology,
						drawCopy.CullMode, drawCopy.BlendEnabled,
						drawCopy.ColorBlendOp, drawCopy.AlphaBlendOp,
						drawCopy.SrcColorBlendFactor, drawCopy.DstColorBlendFactor, drawCopy.SrcAlphaBlendFactor, drawCopy.DstAlphaBlendFactor,
						drawCopy.AlphaToCoverageEnabled,
						drawCopy.DepthTestEnabled, drawCopy.DepthWriteEnabled, drawCopy.DepthCompareOp, drawCopy.ColorWriteMask,
						drawCopy.Viewport.X, drawCopy.Viewport.Y, drawCopy.Viewport.Width, drawCopy.Viewport.Height,
						drawCopy.Scissor.Offset.X, drawCopy.Scissor.Offset.Y, drawCopy.Scissor.Extent.Width, drawCopy.Scissor.Extent.Height,
						_program?.Data?.Name ?? "?prog");

				if (VulkanMeshRenderingConventions.BloomVulkanDiagnosticsEnabled && vertexCount <= 6u)
				{
					Debug.VulkanEvery(
						$"Vulkan.BloomDiag.CmdDraw.{passIndex}.{MaterializationSnapshot.ActiveFrameOpContext?.OutputFrameBufferName}.{vertexCount}",
						TimeSpan.FromSeconds(1),
						"[BloomDiag][Vulkan] CmdDraw fullscreen target='{0}' pass={1} vertices={2} pipeline=0x{3:X} topology={4} blend={5} depthTest={6} depthWrite={7} stencil={8} colorWrite={9} viewport=({10},{11},{12},{13}) scissor=({14},{15},{16},{17}) program='{18}' material='{19}'",
						MaterializationSnapshot.ActiveFrameOpContext?.OutputFrameBufferName ?? "<none>",
						passIndex,
						vertexCount,
						pipeline.Handle,
						fallbackTopology,
						drawCopy.BlendEnabled,
						drawCopy.DepthTestEnabled,
						drawCopy.DepthWriteEnabled,
						drawCopy.StencilTestEnabled,
						drawCopy.ColorWriteMask,
						drawCopy.Viewport.X,
						drawCopy.Viewport.Y,
						drawCopy.Viewport.Width,
						drawCopy.Viewport.Height,
						drawCopy.Scissor.Offset.X,
						drawCopy.Scissor.Offset.Y,
						drawCopy.Scissor.Extent.Width,
						drawCopy.Scissor.Extent.Height,
						_program?.Data?.Name ?? "<program>",
						material.Name ?? "<material>");
				}

				using (StartMeshDrawDetailScope("Vulkan.MeshDraw.CmdDraw"))
					Api!.CmdDraw(commandBuffer, vertexCount, drawInstances, 0, 0);
				drew = true;
			}
		}

		return drew;
	}

	internal bool TryPrepareMeshDrawRecordingState(
		int frameIndex,
		in PendingMeshDraw draw,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		bool depthStencilReadOnly,
		string pipelineName,
		int drawUniformSlot,
		out VulkanPreparedMeshDrawState recordingState,
		out string reason)
	{
		lock (_recordDrawSync)
		{
			return TryPrepareMeshDrawRecordingStateNoLock(
				frameIndex,
				draw,
				renderPass,
				useDynamicRendering,
				dynamicRenderingFormats,
				passIndex,
				passMetadata,
				depthStencilReadOnly,
				pipelineName,
				drawUniformSlot,
				out recordingState,
				out reason);
		}
	}

	private bool TryPrepareMeshDrawRecordingStateNoLock(
		int frameIndex,
		in PendingMeshDraw draw,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		bool depthStencilReadOnly,
		string pipelineName,
		int drawUniformSlot,
		out VulkanPreparedMeshDrawState recordingState,
		out string reason)
	{
		recordingState = default;
		frameIndex = Math.Max(frameIndex, 0);
		if (!TryPrewarmFrameDataForRecordingNoLock(
				draw,
				drawUniformSlot,
				frameIndex,
				out reason))
		{
			return false;
		}

		XRMaterial material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
		if (_program is not { } program)
		{
			reason = "program missing after mesh draw preparation";
			return false;
		}

		if (Mesh?.HasSkinning == true)
			MeshRenderer.PushBoneMatricesToGPU();
		if (Mesh?.HasBlendshapes == true)
			MeshRenderer.PushBlendshapeWeightsToGPU();

		if (!TryRentVertexBufferSnapshot(
				out VkBufferHandle[]? vertexBuffers,
				out uint[]? vertexBindings,
				out int vertexBufferCount,
				out reason))
		{
			return false;
		}

		VulkanPreparedDescriptorSetBinding[]? preparedDescriptorBindings = null;
		uint[]? preparedDynamicOffsets = null;
		uint[]? preparedDescriptorHeapPushDwords = null;
		VulkanPreparedFrameDataPayloadHandle[]?
			preparedFrameDataPayloadHandles = null;
		try
		{
			VulkanPreparedMeshPrimitive primitive0 = default;
			VulkanPreparedMeshPrimitive primitive1 = default;
			VulkanPreparedMeshPrimitive primitive2 = default;
			int primitiveCount = 0;
			bool triangleOnly =
				MeshRenderMaterialResolver.RequiresTriangleOnlyDrawsForCurrentPass();

			if (!TryPrepareIndexedMeshPrimitive(
					material,
					draw,
					renderPass,
					useDynamicRendering,
					dynamicRenderingFormats,
					passIndex,
					passMetadata,
					depthStencilReadOnly,
					pipelineName,
					_triangleIndexBuffer,
					_triangleIndexSize,
					PrimitiveTopology.TriangleList,
					out VulkanPreparedMeshPrimitive trianglePrimitive,
					out reason))
			{
				return false;
			}
			AddPreparedMeshPrimitive(
				trianglePrimitive,
				ref primitive0,
				ref primitive1,
				ref primitive2,
				ref primitiveCount);

			if (!triangleOnly)
			{
				if (!TryPrepareIndexedMeshPrimitive(
						material,
						draw,
						renderPass,
						useDynamicRendering,
						dynamicRenderingFormats,
						passIndex,
						passMetadata,
						depthStencilReadOnly,
						pipelineName,
						_lineIndexBuffer,
						_lineIndexSize,
						PrimitiveTopology.LineList,
						out VulkanPreparedMeshPrimitive linePrimitive,
						out reason))
				{
					return false;
				}
				AddPreparedMeshPrimitive(
					linePrimitive,
					ref primitive0,
					ref primitive1,
					ref primitive2,
					ref primitiveCount);

				if (!TryPrepareIndexedMeshPrimitive(
						material,
						draw,
						renderPass,
						useDynamicRendering,
						dynamicRenderingFormats,
						passIndex,
						passMetadata,
						depthStencilReadOnly,
						pipelineName,
						_pointIndexBuffer,
						_pointIndexSize,
						PrimitiveTopology.PointList,
						out VulkanPreparedMeshPrimitive pointPrimitive,
						out reason))
				{
					return false;
				}
				AddPreparedMeshPrimitive(
					pointPrimitive,
					ref primitive0,
					ref primitive1,
					ref primitive2,
					ref primitiveCount);
			}

			if (primitiveCount == 0)
			{
				if (Mesh is null || Mesh.VertexCount <= 0)
				{
					reason = "mesh has no indexed or non-indexed elements";
					return false;
				}

				PrimitiveTopology fallbackTopology = Mesh.Type switch
				{
					EPrimitiveType.Points => PrimitiveTopology.PointList,
					EPrimitiveType.Lines => PrimitiveTopology.LineList,
					EPrimitiveType.LineStrip => PrimitiveTopology.LineStrip,
					EPrimitiveType.TriangleStrip => PrimitiveTopology.TriangleStrip,
					EPrimitiveType.TriangleFan => PrimitiveTopology.TriangleFan,
					EPrimitiveType.Patches => PrimitiveTopology.PatchList,
					_ => PrimitiveTopology.TriangleList,
				};
				if (triangleOnly && !IsTriangleClassTopology(fallbackTopology))
				{
					reason = $"non-triangle fallback topology {fallbackTopology} is disabled for this pass";
					return false;
				}

				if (!EnsurePipeline(
						material,
						fallbackTopology,
						draw,
						renderPass,
						useDynamicRendering,
						dynamicRenderingFormats,
						passIndex,
						passMetadata,
						depthStencilReadOnly,
						pipelineName,
						allowPipelineCreation: true,
						out Pipeline fallbackPipeline))
				{
					reason = $"pipeline pending for {fallbackTopology}";
					return false;
				}

				primitive0 = new VulkanPreparedMeshPrimitive(
					fallbackPipeline,
					fallbackTopology,
					default,
					default,
					(uint)Mesh.VertexCount,
					Indexed: false);
				primitiveCount = 1;
			}

			UpdateEngineUniformBuffersForDraw(frameIndex, drawUniformSlot, draw);
			UpdateAutoUniformBuffersForDraw(
				frameIndex,
				drawUniformSlot,
				material,
				draw);
			if (!TryRentPreparedFrameDataPayloadHandles(
					program,
					frameIndex,
					drawUniformSlot,
					material,
					draw,
					out preparedFrameDataPayloadHandles,
					out int preparedFrameDataPayloadHandleCount,
					out reason))
			{
				return false;
			}
			TryCaptureCpuDirectDynamicData(
				frameIndex,
				drawUniformSlot,
				draw,
				ResolvePassMask(passIndex));

			DescriptorSet[]? descriptorSets = null;
			DescriptorHeapPushDataPayload? descriptorHeapPushData = null;
			int preparedDescriptorBindingCount = 0;
			int preparedDescriptorHeapPushDwordCount = 0;
			bool usesDescriptorHeap = BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap;
			bool requiresDescriptors =
				program.DescriptorSetLayouts.Count > 0 &&
				program.DescriptorBindings.Count > 0;
			if (requiresDescriptors)
			{
				if (_descriptorSets is not { Length: > 0 })
				{
					reason = "descriptor set array is empty after preparation";
					return false;
				}

				int descriptorSlotIndex =
					ResolveDescriptorFrameIndex(frameIndex, _descriptorSets.Length);
				descriptorSets = _descriptorSets[descriptorSlotIndex];
				if (descriptorSets.Length == 0)
				{
					reason = $"descriptor set array at frame {frameIndex} is empty";
					return false;
				}

				if (_activeDescriptorAllocation?.DescriptorHeapPushData is
						{ Length: > 0 } heapPayloads &&
					(uint)descriptorSlotIndex < (uint)heapPayloads.Length)
				{
					descriptorHeapPushData = heapPayloads[descriptorSlotIndex];
				}

				if (!usesDescriptorHeap &&
					!TryRentPreparedDescriptorBindings(
						program,
						descriptorSets,
						frameIndex,
						drawUniformSlot,
						out preparedDescriptorBindings,
						out preparedDescriptorBindingCount,
						out preparedDynamicOffsets,
						out reason))
				{
					return false;
				}

				if (usesDescriptorHeap &&
					program.DescriptorHeapLayout is
						{ PushDwordCount: > 0 } heapLayout)
				{
					if (descriptorHeapPushData is null ||
						!descriptorHeapPushData.IsValidFor(heapLayout))
					{
						reason =
							"descriptor heap push payload is unavailable after preparation";
						return false;
					}

					preparedDescriptorHeapPushDwordCount =
						heapLayout.PushDwordCount;
					preparedDescriptorHeapPushDwords =
						_preparedUIntArrays.Rent(
							preparedDescriptorHeapPushDwordCount);
					descriptorHeapPushData.Dwords.AsSpan(
						0,
						preparedDescriptorHeapPushDwordCount).CopyTo(
							preparedDescriptorHeapPushDwords);
				}
			}

			recordingState = new VulkanPreparedMeshDrawState(
				this,
				program,
				program.PipelineLayout,
				usesDescriptorHeap,
				preparedDescriptorBindings,
				preparedDescriptorBindingCount,
				preparedDynamicOffsets,
				preparedDescriptorHeapPushDwords,
				preparedDescriptorHeapPushDwordCount,
				vertexBuffers,
				vertexBindings,
				vertexBufferCount,
				primitive0,
				primitive1,
				primitive2,
				primitiveCount,
				frameIndex,
				drawUniformSlot,
				BackendContext.Resources.MappedFrameArena?.Generation ?? 0UL,
				preparedFrameDataPayloadHandles,
				preparedFrameDataPayloadHandleCount,
				CreatePerDrawPushConstants(material, draw),
				draw.Instances);
			vertexBuffers = null;
			vertexBindings = null;
			preparedDescriptorBindings = null;
			preparedDynamicOffsets = null;
			preparedDescriptorHeapPushDwords = null;
			preparedFrameDataPayloadHandles = null;
			reason = "Ready";
			return true;
		}
		finally
		{
			if (vertexBuffers is not null || vertexBindings is not null)
				ReturnRentedVertexBufferSnapshot(vertexBuffers, vertexBindings);
			if (preparedDescriptorBindings is not null || preparedDynamicOffsets is not null)
				ReturnPreparedDescriptorBindings(preparedDescriptorBindings, preparedDynamicOffsets);
			if (preparedDescriptorHeapPushDwords is not null)
				_preparedUIntArrays.Return(preparedDescriptorHeapPushDwords);
			if (preparedFrameDataPayloadHandles is not null)
			{
				_preparedFrameDataPayloadArrays.Return(
					preparedFrameDataPayloadHandles,
					clear: true);
			}
		}
	}

	private bool TryPrepareIndexedMeshPrimitive(
		XRMaterial material,
		in PendingMeshDraw draw,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		bool depthStencilReadOnly,
		string pipelineName,
		VkDataBuffer? indexBuffer,
		IndexSize indexSize,
		PrimitiveTopology topology,
		out VulkanPreparedMeshPrimitive primitive,
		out string reason)
	{
		primitive = default;
		reason = "Ready";
		if (indexBuffer?.BufferHandle is not { Handle: not 0 })
			return true;

		if (!TryResolveIndexBinding(
				indexBuffer,
				indexSize,
				out VkBufferHandle indexHandle,
				out IndexType indexType,
				out uint indexCount) ||
			indexCount == 0)
		{
			reason = $"index binding is unavailable for {topology}";
			return false;
		}

		if (!EnsurePipeline(
				material,
				topology,
				draw,
				renderPass,
				useDynamicRendering,
				dynamicRenderingFormats,
				passIndex,
				passMetadata,
				depthStencilReadOnly,
				pipelineName,
				allowPipelineCreation: true,
				out Pipeline pipeline))
		{
			reason = $"pipeline pending for {topology}";
			return false;
		}

		primitive = new VulkanPreparedMeshPrimitive(
			pipeline,
			topology,
			indexHandle,
			indexType,
			indexCount,
			Indexed: true);
		return true;
	}

	private static void AddPreparedMeshPrimitive(
		in VulkanPreparedMeshPrimitive primitive,
		ref VulkanPreparedMeshPrimitive primitive0,
		ref VulkanPreparedMeshPrimitive primitive1,
		ref VulkanPreparedMeshPrimitive primitive2,
		ref int primitiveCount)
	{
		if (primitive.Pipeline.Handle == 0)
			return;

		switch (primitiveCount)
		{
			case 0:
				primitive0 = primitive;
				break;
			case 1:
				primitive1 = primitive;
				break;
			case 2:
				primitive2 = primitive;
				break;
			default:
				throw new InvalidOperationException(
					"A prepared mesh draw exceeded the bounded primitive count.");
		}

		primitiveCount++;
	}

	internal static bool RecordPreparedMeshDrawState(
		CommandBuffer commandBuffer,
		in VulkanPreparedMeshDrawState recordingState,
		VulkanTrackedCommandEncoder encoder)
	{
		if (recordingState.OwnerIdentity is null ||
			recordingState.Program is null ||
			recordingState.PipelineLayout.Handle == 0 ||
			recordingState.PrimitiveCount <= 0 ||
			!recordingState.HasValidFrameDataPayloadHandles())
		{
			return false;
		}

		for (int primitiveIndex = 0;
			 primitiveIndex < recordingState.PrimitiveCount;
			 primitiveIndex++)
		{
			VulkanPreparedMeshPrimitive primitive =
				recordingState.GetPrimitive(primitiveIndex);
			if (primitive.Pipeline.Handle == 0 || primitive.ElementCount == 0)
				return false;

			encoder.BindPipeline(commandBuffer, primitive.Pipeline);
			if (recordingState.VertexBufferCount > 0)
			{
				if (recordingState.VertexBuffers is null ||
					recordingState.VertexBindings is null)
				{
					return false;
				}

				for (int bufferIndex = 0;
					 bufferIndex < recordingState.VertexBufferCount;
					 bufferIndex++)
				{
					encoder.BindVertexBuffer(
						commandBuffer,
						recordingState.VertexBindings[bufferIndex],
						recordingState.VertexBuffers[bufferIndex]);
				}
			}

			encoder.PushConstants(
				commandBuffer,
				recordingState.PipelineLayout,
				VulkanPipelineManager.CommonPushConstantStages,
				recordingState.PushConstants);

			if (recordingState.UsesDescriptorHeap)
			{
				if (!encoder.TryPushDescriptorHeapProgramData(
						commandBuffer,
						recordingState.Program,
						recordingState.DescriptorHeapPushDwords,
						recordingState.DescriptorHeapPushDwordCount))
				{
					return false;
				}
			}
			else if (!BindPreparedMeshDescriptorSets(
				encoder,
				commandBuffer,
				recordingState))
			{
				return false;
			}

			if (primitive.Indexed)
			{
				encoder.BindIndexBuffer(commandBuffer, primitive.IndexBuffer, primitive.IndexType);
				encoder.Runtime.Api.CmdDrawIndexed(
					commandBuffer,
					primitive.ElementCount,
					recordingState.InstanceCount,
					0,
					0,
					0);
			}
			else
			{
				encoder.Runtime.Api.CmdDraw(
					commandBuffer,
					primitive.ElementCount,
					recordingState.InstanceCount,
					0,
					0);
			}
		}

		return true;
	}

	internal static void ReturnPreparedMeshDrawStateBuffers(
		in VulkanPreparedMeshDrawState recordingState)
	{
		recordingState.OwnerIdentity.ReturnRentedVertexBufferSnapshot(
			recordingState.VertexBuffers,
			recordingState.VertexBindings);
		recordingState.OwnerIdentity.ReturnPreparedDescriptorBindings(
			recordingState.DescriptorBindings,
			recordingState.DynamicOffsets);
		if (recordingState.DescriptorHeapPushDwords is not null)
			recordingState.OwnerIdentity._preparedUIntArrays.Return(
				recordingState.DescriptorHeapPushDwords);
		if (recordingState.FrameDataPayloadHandles is not null)
		{
			recordingState.OwnerIdentity._preparedFrameDataPayloadArrays.Return(
				recordingState.FrameDataPayloadHandles,
				clear: true);
		}
	}

	private bool TryRentPreparedFrameDataPayloadHandles(
		VkRenderProgram program,
		int frameIndex,
		int drawUniformSlot,
		XRMaterial material,
		in PendingMeshDraw draw,
		out VulkanPreparedFrameDataPayloadHandle[]? handles,
		out int handleCount,
		out string reason)
	{
		handles = null;
		handleCount = 0;
		reason = "Ready";

		int candidateCount = 0;
		for (int bindingIndex = 0;
			 bindingIndex < program.DescriptorBindings.Count;
			 bindingIndex++)
		{
			DescriptorType descriptorType =
				program.DescriptorBindings[bindingIndex].DescriptorType;
			if (descriptorType is DescriptorType.UniformBuffer or
				DescriptorType.UniformBufferDynamic)
			{
				candidateCount++;
			}
		}

		if (candidateCount == 0)
			return true;

		handles = _preparedFrameDataPayloadArrays.Rent(candidateCount);
		ulong arenaGeneration = BackendContext.Resources.MappedFrameArena?.Generation ?? 0UL;
		ulong materialGeneration = ComputeMaterialPublicationGeneration(
			material.BindingLayoutVersion,
			material.BindingValueVersion,
			draw.ProgramBindingSnapshot?.RuntimeUniformNameSignature ?? 0UL,
			draw.ProgramBindingSnapshot
				?.MutableLegacyUniformValueSignature ?? 0UL);

		for (int bindingIndex = 0;
			 bindingIndex < program.DescriptorBindings.Count;
			 bindingIndex++)
		{
			DescriptorBindingInfo binding =
				program.DescriptorBindings[bindingIndex];
			if (binding.DescriptorType is not (
				DescriptorType.UniformBuffer or
				DescriptorType.UniformBufferDynamic))
			{
				continue;
			}

			if (program.TryGetAutoUniformBlock(
					binding.Set,
					binding.Binding,
					out AutoUniformBlockInfo block))
			{
				if (!_autoUniformBuffers.TryGetValue(
						block.InstanceName,
						out AutoUniformBuffer[]? autoBuffers) ||
					autoBuffers.Length == 0)
				{
					reason =
						$"auto-uniform payload '{block.InstanceName}' is unavailable after publication";
					return false;
				}

				AutoUniformBuffer target = autoBuffers[
					ResolvePublishedAutoUniformBufferIndex(
						block,
						frameIndex,
						drawUniformSlot,
						autoBuffers.Length)];
				EVulkanBindingFrequencyMask frequencyMask =
					program.BindingSchema is { } programSchema &&
					programSchema.TryGetAutoUniformBlock(
						block.InstanceName,
						out VulkanAutoUniformBindingSchema blockSchema)
						? blockSchema.FrequencyMask
						: EVulkanBindingFrequencyMask.All;
				handles[handleCount++] =
					new VulkanPreparedFrameDataPayloadHandle(
						this,
						target.Buffer,
						target.Offset,
						Math.Min(target.Size, Math.Max(block.Size, 1u)),
						binding.Set,
						binding.Binding,
						frameIndex,
						drawUniformSlot,
						arenaGeneration,
						frequencyMask,
						draw.AutoUniformPublication,
						materialGeneration);
				continue;
			}

			string engineUniformName =
				NormalizeEngineUniformName(binding.Name ?? string.Empty);
			uint engineUniformSize =
				GetEngineUniformSize(binding.Name ?? string.Empty);
			if (engineUniformSize == 0)
				continue;
			if (!_engineUniformBuffers.TryGetValue(
					engineUniformName,
					out EngineUniformBuffer[]? engineBuffers) ||
				engineBuffers.Length == 0)
			{
				reason =
					$"engine-uniform payload '{engineUniformName}' is unavailable after publication";
				return false;
			}

			EngineUniformBuffer engineTarget = engineBuffers[
				ResolveUniformBufferIndex(
					frameIndex,
					drawUniformSlot,
					engineBuffers.Length)];
			handles[handleCount++] =
				new VulkanPreparedFrameDataPayloadHandle(
					this,
					engineTarget.Buffer,
					engineTarget.Offset,
					Math.Min(engineTarget.Size, engineUniformSize),
					binding.Set,
					binding.Binding,
					frameIndex,
					drawUniformSlot,
					arenaGeneration,
					EVulkanBindingFrequencyMask.All,
					draw.AutoUniformPublication,
					materialGeneration);
		}

		return true;
	}

	private bool TryRentPreparedDescriptorBindings(
		VkRenderProgram program,
		DescriptorSet[] descriptorSets,
		int frameIndex,
		int drawUniformSlot,
		out VulkanPreparedDescriptorSetBinding[]? preparedBindings,
		out int preparedBindingCount,
		out uint[]? dynamicOffsets,
		out string reason)
	{
		preparedBindings = null;
		preparedBindingCount = 0;
		dynamicOffsets = null;
		reason = "Ready";

		int totalDynamicOffsetCount = 0;
		for (int setIndex = 0; setIndex < descriptorSets.Length; setIndex++)
		{
			if (descriptorSets[setIndex].Handle == 0)
				continue;

			preparedBindingCount++;
			int setDynamicOffsetCount = 0;
			for (int bindingIndex = 0;
				 bindingIndex < program.DescriptorBindings.Count;
				 bindingIndex++)
			{
				DescriptorBindingInfo binding =
					program.DescriptorBindings[bindingIndex];
				if (binding.Set != (uint)setIndex ||
					binding.DescriptorType != DescriptorType.UniformBufferDynamic)
				{
					continue;
				}

				setDynamicOffsetCount = checked(
					setDynamicOffsetCount +
					(int)VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding));
			}

			if (setDynamicOffsetCount > 64)
			{
				reason =
					$"descriptor set {setIndex} has {setDynamicOffsetCount} dynamic uniform offsets; the bounded limit is 64";
				return false;
			}

			totalDynamicOffsetCount = checked(
				totalDynamicOffsetCount + setDynamicOffsetCount);
		}

		if (preparedBindingCount == 0)
			return true;

		preparedBindings =
			_preparedDescriptorBindingArrays.Rent(preparedBindingCount);
		if (totalDynamicOffsetCount > 0)
			dynamicOffsets = _preparedUIntArrays.Rent(totalDynamicOffsetCount);

		int preparedBindingIndex = 0;
		int dynamicOffsetIndex = 0;
		for (int setIndex = 0; setIndex < descriptorSets.Length; setIndex++)
		{
			DescriptorSet descriptorSet = descriptorSets[setIndex];
			if (descriptorSet.Handle == 0)
				continue;

			int dynamicOffsetStart = dynamicOffsetIndex;
			for (int bindingIndex = 0;
				 bindingIndex < program.DescriptorBindings.Count;
				 bindingIndex++)
			{
				DescriptorBindingInfo binding =
					program.DescriptorBindings[bindingIndex];
				if (binding.Set != (uint)setIndex ||
					binding.DescriptorType != DescriptorType.UniformBufferDynamic)
				{
					continue;
				}

				uint descriptorCount =
					VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding);
				if (descriptorCount != 1 ||
					dynamicOffsets is null ||
					!TryResolveDynamicUniformOffset(
						program,
						binding,
						frameIndex,
						drawUniformSlot,
						out uint offset))
				{
					reason =
						$"dynamic uniform '{binding.Name}' in descriptor set {setIndex} could not resolve a bounded offset";
					return false;
				}

				dynamicOffsets[dynamicOffsetIndex++] = offset;
			}

			preparedBindings[preparedBindingIndex++] =
				new VulkanPreparedDescriptorSetBinding(
					descriptorSet,
					(uint)setIndex,
					dynamicOffsetStart,
					dynamicOffsetIndex - dynamicOffsetStart);
		}

		return true;
	}

	private static bool BindPreparedMeshDescriptorSets(
		VulkanTrackedCommandEncoder encoder,
		CommandBuffer commandBuffer,
		in VulkanPreparedMeshDrawState recordingState)
	{
		if (recordingState.DescriptorBindingCount == 0)
			return true;
		if (recordingState.DescriptorBindings is not { } descriptorBindings ||
			descriptorBindings.Length < recordingState.DescriptorBindingCount)
		{
			return false;
		}

		if (!encoder.TryAcquireFrameDataLease(
				commandBuffer,
				recordingState.OwnerIdentity,
				recordingState.DrawUniformSlot,
				recordingState.FrameDataGeneration,
				out _))
		{
			return false;
		}

		Span<DescriptorSet> oneSet = stackalloc DescriptorSet[1];
		for (int bindingIndex = 0;
			 bindingIndex < recordingState.DescriptorBindingCount;
			 bindingIndex++)
		{
			VulkanPreparedDescriptorSetBinding binding =
				descriptorBindings[bindingIndex];
			if (binding.DescriptorSet.Handle == 0 ||
				binding.DynamicOffsetStart < 0 ||
				binding.DynamicOffsetCount < 0)
			{
				return false;
			}

			ReadOnlySpan<uint> offsets = ReadOnlySpan<uint>.Empty;
			if (binding.DynamicOffsetCount > 0)
			{
				if (recordingState.DynamicOffsets is not { } dynamicOffsets ||
					binding.DynamicOffsetStart >
						dynamicOffsets.Length - binding.DynamicOffsetCount)
				{
					return false;
				}

				offsets = dynamicOffsets.AsSpan(
					binding.DynamicOffsetStart,
					binding.DynamicOffsetCount);
			}

			oneSet[0] = binding.DescriptorSet;
			encoder.BindDescriptorSet(
				commandBuffer,
				recordingState.PipelineLayout,
				binding.SetIndex,
				oneSet[0],
				offsets);
		}

		return true;
	}

	private void ReturnPreparedDescriptorBindings(
		VulkanPreparedDescriptorSetBinding[]? preparedBindings,
		uint[]? dynamicOffsets)
	{
		if (preparedBindings is not null)
			_preparedDescriptorBindingArrays.Return(preparedBindings, clear: true);
		if (dynamicOffsets is not null)
			_preparedUIntArrays.Return(dynamicOffsets);
	}

	internal bool RecordIndirectDrawState(
		CommandBuffer commandBuffer,
		in PendingMeshDraw draw,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		bool depthStencilReadOnly,
		string pipelineName,
		string targetName,
		int drawUniformSlot,
		out IndexType indexType)
	{
		indexType = IndexType.Uint32;
		var material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
        bool preparedForRecord = draw.PreparedProgram is { } preparedProgram
            ? TryPrepareCapturedProgramForRecording(material, preparedProgram, draw.PreparedProgramIdentity, draw.PreparedProgramLinkGeneration, draw.ProgramBindingSnapshot, drawUniformSlot, out global::System.String prepareReason)
            : TryPrepareForRendering(material, out prepareReason);
        if (!preparedForRecord)
		{
			Debug.VulkanWarningEvery(
				$"Vulkan.IndirectDraw.PrepareSkip.{MeshRenderer.Name ?? "IndirectRenderer"}.{prepareReason}",
				TimeSpan.FromSeconds(2),
				"[Vulkan] Skipping indirect draw because atlas renderer preparation failed: {0}. {1}",
				prepareReason,
				LastPrepareDetail);
			return false;
		}

		if (!TryResolveIndexBinding(_triangleIndexBuffer, _triangleIndexSize, out VkBufferHandle indexHandle, out indexType, out uint indexCount) ||
			indexCount == 0)
		{
			Debug.VulkanWarningEvery(
				$"Vulkan.IndirectDraw.IndexMissing.{MeshRenderer.Name ?? "IndirectRenderer"}",
				TimeSpan.FromSeconds(2),
				"[Vulkan] Skipping indirect draw because the atlas triangle index buffer is not ready.");
			return false;
		}

		if (!EnsurePipeline(material, PrimitiveTopology.TriangleList, draw, renderPass, useDynamicRendering, dynamicRenderingFormats, passIndex, passMetadata, depthStencilReadOnly, pipelineName, allowPipelineCreation: false, out var pipeline))
			return false;

		CommandOperations.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

		if (!BindVertexBuffersForCurrentPipeline(commandBuffer))
			return false;

		if (_program?.Data is { } programData)
			NotifyDrawUniforms(material, programData, draw);

		int frameIndex = ResolveCommandBufferIndex(commandBuffer);
		if (frameIndex < 0)
			frameIndex = 0;

		if (!BindDescriptorsIfAvailable(commandBuffer, material, draw, drawUniformSlot, frameIndex, passIndex))
			return false;

		PushPerDrawConstants(commandBuffer, material, draw);
		CommandOperations.BindIndexBufferTracked(commandBuffer, indexHandle, 0, indexType);
		return true;
	}

	internal readonly record struct IndirectDrawRecordingState(
		VkMeshRenderer OwnerIdentity,
		VkRenderProgram Program,
		Pipeline Pipeline,
		PipelineLayout PipelineLayout,
		DescriptorSet[]? DescriptorSets,
		DescriptorHeapPushDataPayload? DescriptorHeapPushData,
		VkBufferHandle[]? VertexBuffers,
		uint[]? VertexBindings,
		int VertexBufferCount,
		VkBufferHandle IndexBuffer,
		IndexType IndexType,
		int FrameIndex,
		int DrawUniformSlot,
		ulong FrameDataGeneration,
		MeshDrawPushConstants PushConstants);

	internal bool TryPrepareIndirectDrawRecordingState(
		uint imageIndex,
		in PendingMeshDraw draw,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		bool depthStencilReadOnly,
		string pipelineName,
		int drawUniformSlot,
		out IndirectDrawRecordingState recordingState,
		out string reason)
	{
		recordingState = default;
		reason = "Ready";

        var material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
        bool preparedForRecord = draw.PreparedProgram is { } preparedProgram
			? TryPrepareCapturedProgramForRecording(material, preparedProgram, draw.PreparedProgramIdentity, draw.PreparedProgramLinkGeneration, draw.ProgramBindingSnapshot, drawUniformSlot, out reason)
			: TryPrepareForRendering(material, out reason);
		if (!preparedForRecord)
			return false;

		if (!TryResolveIndexBinding(_triangleIndexBuffer, _triangleIndexSize, out VkBufferHandle indexHandle, out IndexType indexType, out uint indexCount) ||
			indexCount == 0)
		{
			reason = "atlas triangle index buffer is not ready";
			return false;
		}

		if (!EnsurePipeline(material, PrimitiveTopology.TriangleList, draw, renderPass, useDynamicRendering, dynamicRenderingFormats, passIndex, passMetadata, depthStencilReadOnly, pipelineName, allowPipelineCreation: true, out var pipeline))
		{
			reason = "pipeline pending";
			return false;
		}

		if (_program is not { } program)
		{
			reason = "program missing after pipeline preparation";
			return false;
		}

		if (!TryRentVertexBufferSnapshot(out Silk.NET.Vulkan.Buffer[]? vertexBuffers, out global::System.UInt32[]? vertexBindings, out int vertexBufferCount, out reason))
			return false;

		try
		{
			if (program.Data is { } programData)
				NotifyDrawUniforms(material, programData, draw);

			DescriptorSet[]? descriptorSets = null;
			DescriptorHeapPushDataPayload? descriptorHeapPushData = null;
			bool requiresDescriptors = program.DescriptorSetLayouts.Count > 0 && program.DescriptorBindings.Count > 0;
			if (requiresDescriptors)
			{
				int frameIndex = imageIndex > int.MaxValue ? int.MaxValue : (int)imageIndex;
				if (!EnsureDescriptorSets(material, drawUniformSlot, frameIndex, draw.ProgramBindingSnapshot))
				{
					reason = "descriptor sets pending";
					ReturnRentedVertexBufferSnapshot(vertexBuffers, vertexBindings);
					vertexBuffers = null;
					vertexBindings = null;
					return false;
				}

				if (_descriptorSets is null || _descriptorSets.Length == 0)
				{
					reason = "descriptor set array is empty";
					ReturnRentedVertexBufferSnapshot(vertexBuffers, vertexBindings);
					vertexBuffers = null;
					vertexBindings = null;
					return false;
				}

				int descriptorSlotIndex = ResolveDescriptorFrameIndex(frameIndex, _descriptorSets.Length);
				UpdateEngineUniformBuffersForDraw(frameIndex, drawUniformSlot, draw);
				UpdateAutoUniformBuffersForDraw(frameIndex, drawUniformSlot, material, draw);
				TryCaptureCpuDirectDynamicData(frameIndex, drawUniformSlot, draw, ResolvePassMask(passIndex));

				descriptorSets = _descriptorSets[descriptorSlotIndex];
				if (descriptorSets.Length == 0)
				{
					reason = $"descriptor set array at imageIndex {frameIndex}, drawSlot {drawUniformSlot} is empty";
					ReturnRentedVertexBufferSnapshot(vertexBuffers, vertexBindings);
					vertexBuffers = null;
					vertexBindings = null;
					return false;
				}

				if (_activeDescriptorAllocation?.DescriptorHeapPushData is { Length: > 0 } heapPayloads &&
					(uint)descriptorSlotIndex < (uint)heapPayloads.Length)
				{
					descriptorHeapPushData = heapPayloads[descriptorSlotIndex];
				}
			}

			recordingState = new IndirectDrawRecordingState(
				this,
				program,
				pipeline,
				program.PipelineLayout,
				descriptorSets,
				descriptorHeapPushData,
				vertexBuffers,
				vertexBindings,
				vertexBufferCount,
				indexHandle,
				indexType,
				(int)Math.Min(imageIndex, int.MaxValue),
				drawUniformSlot,
				BackendContext.Resources.MappedFrameArena?.Generation ?? 0UL,
				CreatePerDrawPushConstants(material, draw));

			vertexBuffers = null;
			vertexBindings = null;
			return true;
		}
		finally
		{
			if (vertexBuffers is not null || vertexBindings is not null)
				ReturnRentedVertexBufferSnapshot(vertexBuffers, vertexBindings);
		}
	}

	internal bool RecordPreparedIndirectDrawState(CommandBuffer commandBuffer, in IndirectDrawRecordingState recordingState)
	{
		if (recordingState.Pipeline.Handle == 0 ||
			recordingState.PipelineLayout.Handle == 0 ||
			recordingState.Program is null)
			return false;

		CommandOperations.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, recordingState.Pipeline);

		if (recordingState.VertexBufferCount > 0)
		{
			if (recordingState.VertexBuffers is null || recordingState.VertexBindings is null)
				return false;

			for (int i = 0; i < recordingState.VertexBufferCount; i++)
				CommandOperations.BindVertexBufferTracked(commandBuffer, recordingState.VertexBindings[i], recordingState.VertexBuffers[i], 0);
		}

		CommandOperations.PushConstantsTracked(
			commandBuffer,
			recordingState.PipelineLayout,
			VulkanMeshRenderingConventions.CommonPushConstantStageFlags,
			0,
			recordingState.PushConstants);

		if (BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
		{
			if (!CommandOperations.TryPushDescriptorHeapProgramData(commandBuffer, recordingState.Program, recordingState.DescriptorHeapPushData, out string heapReason))
			{
				WarnOnce($"Skipping prepared draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because descriptor heap push failed: {heapReason}");
				return false;
			}
		}
		else if (recordingState.DescriptorSets is { Length: > 0 } descriptorSets &&
			!BindMeshDescriptorSets(
				commandBuffer,
				recordingState.Program,
				recordingState.PipelineLayout,
				descriptorSets,
					recordingState.FrameIndex,
					recordingState.DrawUniformSlot,
					recordingState.FrameDataGeneration))
		{
			return false;
		}

			CommandOperations.BindIndexBufferTracked(commandBuffer, recordingState.IndexBuffer, 0, recordingState.IndexType);
		return true;
	}

	internal static void ReturnIndirectDrawRecordingStateBuffers(in IndirectDrawRecordingState recordingState)
		=> recordingState.OwnerIdentity?.ReturnRentedVertexBufferSnapshot(
			recordingState.VertexBuffers,
			recordingState.VertexBindings);

	private bool TryRentVertexBufferSnapshot(
		out VkBufferHandle[]? vertexBuffers,
		out uint[]? vertexBindings,
		out int vertexBufferCount,
		out string reason)
	{
		lock (_bufferStateSync)
		{
			vertexBufferCount = _vertexBindings.Length;
			reason = "Ready";
			vertexBuffers = null;
			vertexBindings = null;

			if (vertexBufferCount == 0)
				return true;

			vertexBuffers = _preparedVertexBufferArrays.Rent(vertexBufferCount);
			vertexBindings = _preparedUIntArrays.Rent(vertexBufferCount);

			for (int i = 0; i < vertexBufferCount; i++)
			{
				VertexInputBindingDescription binding = _vertexBindings[i];
				if (!_vertexBuffersByBinding.TryGetValue(binding.Binding, out VkDataBuffer? sourceBuffer))
				{
					reason = $"vertex binding {binding.Binding} has no backing buffer";
					ReturnRentedVertexBufferSnapshot(vertexBuffers, vertexBindings);
					vertexBuffers = null;
					vertexBindings = null;
					return false;
				}

				if (!sourceBuffer.TryEnsureReadyForRendering(BackendContext.Resources.AllowSynchronousResourceUploads))
				{
					reason = $"vertex binding {binding.Binding} buffer is not ready";
					ReturnRentedVertexBufferSnapshot(vertexBuffers, vertexBindings);
					vertexBuffers = null;
					vertexBindings = null;
					return false;
				}

				if (sourceBuffer.BufferHandle is not { } handle || handle.Handle == 0)
				{
					reason = $"vertex binding {binding.Binding} buffer is not allocated";
					ReturnRentedVertexBufferSnapshot(vertexBuffers, vertexBindings);
					vertexBuffers = null;
					vertexBindings = null;
					return false;
				}

				vertexBindings[i] = binding.Binding;
				vertexBuffers[i] = handle;
			}

			return true;
		}
	}

	private void ReturnRentedVertexBufferSnapshot(VkBufferHandle[]? vertexBuffers, uint[]? vertexBindings)
	{
		if (vertexBuffers is not null)
			_preparedVertexBufferArrays.Return(vertexBuffers, clear: true);
		if (vertexBindings is not null)
			_preparedUIntArrays.Return(vertexBindings);
	}

	private void NotifyDrawUniforms(XRMaterial material, XRRenderProgram programData, in PendingMeshDraw draw)
	{
		if (draw.ProgramBindingSnapshot is { } snapshot && _program is not null)
		{
			LogGizmoBindingSnapshot(material, snapshot, "apply");
			_program.ApplyBindingSnapshot(snapshot);
			return;
		}

		_program?.ClearBindings();
		CommandOperations.SetMaterialUniforms(material, programData, _program, draw.ShadowUniformState);
		MeshRenderer.OnSettingUniforms(programData, programData);
		MeshRenderMaterialResolver.ApplyShadowUniforms(programData, material, draw.ShadowUniformState);
	}

	private static bool IsTriangleClassTopology(PrimitiveTopology topology)
		=> topology is PrimitiveTopology.TriangleList or
			PrimitiveTopology.TriangleStrip or
			PrimitiveTopology.TriangleFan or
			PrimitiveTopology.PatchList;

	/// <summary>
	/// Binds all vertex buffers required by the current pipeline's input bindings.
	/// Returns false (and warns) if any binding has no backing buffer.
	/// </summary>
	private bool BindVertexBuffersForCurrentPipeline(CommandBuffer commandBuffer)
	{
		lock (_bufferStateSync)
		{
			if (_vertexBindings.Length == 0)
				return true;

			foreach (VertexInputBindingDescription binding in _vertexBindings)
			{
				if (!_vertexBuffersByBinding.TryGetValue(binding.Binding, out VkDataBuffer? sourceBuffer))
				{
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because vertex binding {binding.Binding} has no backing buffer.");
					return false;
				}

				if (!sourceBuffer.TryEnsureReadyForRendering(BackendContext.Resources.AllowSynchronousResourceUploads))
				{
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because vertex binding {binding.Binding} buffer is not ready.");
					return false;
				}

				if (sourceBuffer.BufferHandle is not { } handle || handle.Handle == 0)
				{
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because vertex binding {binding.Binding} buffer is not allocated.");
					return false;
				}

				_singleVertexBindingBuffer[0] = handle;
				CommandOperations.BindVertexBuffersTracked(commandBuffer, binding.Binding, _singleVertexBindingBuffer, _singleVertexBindingOffset);
			}

			return true;
		}
	}

	/// <summary>
	/// Binds descriptor sets for the current mesh draw. Mesh draws must use the
	/// renderer-owned descriptor path because it carries per-draw engine and auto
	/// uniform buffers in addition to material resources.
	/// </summary>
	private bool BindDescriptorsIfAvailable(CommandBuffer commandBuffer, XRMaterial material, in PendingMeshDraw draw, int drawUniformSlot, int frameDataImageIndex, int passIndex)
	{
		if (_program is null)
			return true;

		string meshName = Mesh?.Name ?? "UnnamedMesh";
		string programName = _program.Data?.Name ?? "UnnamedProgram";
		string materialName = material.Name ?? "UnnamedMaterial";
		int imageIndex = Math.Max(frameDataImageIndex, 0);
		TryCaptureCpuDirectDynamicData(imageIndex, drawUniformSlot, draw, ResolvePassMask(passIndex));

		bool requiresDescriptors = _program.DescriptorSetLayouts.Count > 0 && _program.DescriptorBindings.Count > 0;
		if (!requiresDescriptors)
			return true;

		if (!EnsureDescriptorSets(material, drawUniformSlot, imageIndex, draw.ProgramBindingSnapshot))
		{
			WarnOnce($"[DescFail] mesh={meshName} prog={programName} mat={materialName} reason=EnsureDescriptorSets returned false");
			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
				programName,
				"descriptor-set",
				materialName,
				0,
				0,
				skippedDraw: true,
				skippedDispatch: false,
				$"mesh={meshName} EnsureDescriptorSets returned false");
			return false;
		}

		if (!TryRefreshFrameSourceDescriptorSetsForDraw(imageIndex, drawUniformSlot, material, draw.ProgramBindingSnapshot, commandBuffer, out string frameSourceDescriptorReason))
		{
			WarnOnce($"[DescFail] mesh={meshName} prog={programName} mat={materialName} reason={frameSourceDescriptorReason}");
			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
				programName,
				"descriptor-set",
				materialName,
				0,
				0,
				skippedDraw: true,
				skippedDispatch: false,
				$"mesh={meshName} {frameSourceDescriptorReason}");
			return false;
		}

		if (_descriptorSets is null || _descriptorSets.Length == 0)
		{
			WarnOnce($"[DescFail] mesh={meshName} prog={programName} mat={materialName} reason=descriptor set array is null or empty");
			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
				programName,
				"descriptor-set",
				materialName,
				0,
				0,
				skippedDraw: true,
				skippedDispatch: false,
				$"mesh={meshName} descriptor set array is null or empty");
			return false;
		}

		int descriptorSlotIndex = ResolveDescriptorFrameIndex(imageIndex, _descriptorSets.Length);

		UpdateEngineUniformBuffersForDraw(imageIndex, drawUniformSlot, draw);
		UpdateAutoUniformBuffersForDraw(imageIndex, drawUniformSlot, material, draw);
		TryCaptureCpuDirectDynamicData(imageIndex, drawUniformSlot, draw, ResolvePassMask(passIndex));

		DescriptorSet[] sets = _descriptorSets[descriptorSlotIndex];
		if (sets.Length == 0)
		{
			WarnOnce($"[DescFail] mesh={meshName} prog={programName} mat={materialName} reason=descriptor set array at imageIndex {imageIndex}, drawSlot {drawUniformSlot} is empty");
			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
				programName,
				"descriptor-set",
				materialName,
				0,
				0,
				skippedDraw: true,
				skippedDispatch: false,
				$"mesh={meshName} descriptor set array at imageIndex {imageIndex}, drawSlot {drawUniformSlot} is empty");
			return false;
		}

		if (BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
		{
			DescriptorHeapPushDataPayload? payload = _activeDescriptorAllocation?.DescriptorHeapPushData is { Length: > 0 } heapPayloads &&
				(uint)descriptorSlotIndex < (uint)heapPayloads.Length
					? heapPayloads[descriptorSlotIndex]
					: null;
			if (!CommandOperations.TryPushDescriptorHeapProgramData(commandBuffer, _program, payload, out string heapReason))
			{
				WarnOnce($"[DescFail] mesh={meshName} prog={programName} mat={materialName} reason=descriptor heap push failed: {heapReason}");
				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
					programName,
					"descriptor-heap",
					materialName,
					0,
					0,
					skippedDraw: true,
					skippedDispatch: false,
					$"mesh={meshName} descriptor heap push failed: {heapReason}");
				return false;
			}

			return true;
		}

		return BindMeshDescriptorSets(
			commandBuffer,
			_program,
			_program.PipelineLayout,
			sets,
			imageIndex,
			drawUniformSlot);
	}

	private static uint ResolvePassMask(int passIndex)
		=> (uint)passIndex < 32u ? 1u << passIndex : 1u;

	private bool BindMeshDescriptorSets(
		CommandBuffer commandBuffer,
		VkRenderProgram program,
		PipelineLayout pipelineLayout,
		DescriptorSet[] sets,
		int frameIndex,
		int drawUniformSlot,
		ulong sealedFrameDataGeneration = 0)
	{
		if (!CommandOperations.TryAcquireMappedFrameArenaRecordingLease(
				commandBuffer,
				this,
				drawUniformSlot,
				sealedFrameDataGeneration,
				out string leaseReason))
		{
			WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because its frame-data generation lease was rejected: {leaseReason}.");
			return false;
		}

		Span<uint> dynamicOffsetScratch = stackalloc uint[64];
		Span<DescriptorSet> oneSet = stackalloc DescriptorSet[1];
		for (int setIndex = 0; setIndex < sets.Length; setIndex++)
		{
			if (sets[setIndex].Handle == 0)
				continue;

			int dynamicOffsetCount = 0;
			for (int i = 0; i < program.DescriptorBindings.Count; i++)
			{
				DescriptorBindingInfo binding = program.DescriptorBindings[i];
				if (binding.Set == (uint)setIndex && binding.DescriptorType == DescriptorType.UniformBufferDynamic)
					dynamicOffsetCount += checked((int)VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding));
			}

			if (dynamicOffsetCount > 64)
			{
				WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because set {setIndex} has {dynamicOffsetCount} dynamic uniform offsets; the bounded limit is 64.");
				return false;
			}

			Span<uint> dynamicOffsets = dynamicOffsetScratch[..dynamicOffsetCount];
			int offsetIndex = 0;
			for (int i = 0; i < program.DescriptorBindings.Count; i++)
			{
				DescriptorBindingInfo binding = program.DescriptorBindings[i];
				if (binding.Set != (uint)setIndex || binding.DescriptorType != DescriptorType.UniformBufferDynamic)
					continue;

				uint descriptorCount = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding);
				if (descriptorCount != 1 ||
					!TryResolveDynamicUniformOffset(
						program,
						binding,
						frameIndex,
						drawUniformSlot,
						out uint offset))
				{
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because dynamic uniform '{binding.Name}' could not resolve a bounded offset.");
					return false;
				}
				dynamicOffsets[offsetIndex++] = offset;
			}

			oneSet[0] = sets[setIndex];
			CommandOperations.BindDescriptorSetsTracked(
				commandBuffer,
				PipelineBindPoint.Graphics,
				pipelineLayout,
				(uint)setIndex,
				oneSet,
				dynamicOffsets);
		}
		return true;
	}

	private bool TryResolveDynamicUniformOffset(
		VkRenderProgram program,
		DescriptorBindingInfo binding,
		int frameIndex,
		int drawUniformSlot,
		out uint dynamicOffset)
	{
		dynamicOffset = 0;
		if (program.TryGetAutoUniformBlock(
				binding.Set,
				binding.Binding,
				out AutoUniformBlockInfo block) &&
			_autoUniformBuffers.TryGetValue(block.InstanceName, out AutoUniformBuffer[]? autoBuffers) &&
			autoBuffers.Length > 0)
		{
			AutoUniformBuffer target = autoBuffers[
				ResolvePublishedAutoUniformBufferIndex(
					block,
					frameIndex,
					drawUniformSlot,
					autoBuffers.Length)];
			if (target.Offset <= uint.MaxValue)
			{
				dynamicOffset = (uint)target.Offset;
				return true;
			}
			return false;
		}

		string name = NormalizeEngineUniformName(binding.Name ?? string.Empty);
		if (!_engineUniformBuffers.TryGetValue(name, out EngineUniformBuffer[]? engineBuffers) || engineBuffers.Length == 0)
			return false;

		EngineUniformBuffer engineTarget = engineBuffers[ResolveUniformBufferIndex(frameIndex, drawUniformSlot, engineBuffers.Length)];
		if (engineTarget.Offset > uint.MaxValue)
			return false;
		dynamicOffset = (uint)engineTarget.Offset;
		return true;
	}

	internal bool TryRefreshReusableCommandBufferFrameData(uint imageIndex, in PendingMeshDraw draw, int drawUniformSlot, bool refreshMaterialUniforms = true)
		=> TryRefreshReusableCommandBufferFrameData(imageIndex, draw, drawUniformSlot, out _, refreshMaterialUniforms);

	internal bool TryPrewarmFrameDataForRecording(
		in PendingMeshDraw draw,
		int drawUniformSlot,
		int frameIndex,
		out string reason)
	{
		// The primary reuse refresh and secondary recording paths can prepare the
		// same renderer concurrently. They mutate the active program, descriptor
		// allocation, and dynamic UBO state, so they must share RecordDraw's lock.
		lock (_recordDrawSync)
			return TryPrewarmFrameDataForRecordingNoLock(draw, drawUniformSlot, frameIndex, out reason);
	}

	/// <summary>
	/// Transitions the native images published by the exact descriptor allocation
	/// that this draw will bind. A logical texture can rotate to a newer physical
	/// generation after submission, so re-resolving the texture is not sufficient.
	/// </summary>
	internal bool TryTransitionPreparedDescriptorImagesForSampling(
		CommandBuffer commandBuffer,
		in VulkanPreparedMeshDrawState recordingState,
		XRFrameBuffer? target,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata)
	{
		if (recordingState.UsesDescriptorHeap ||
			recordingState.DescriptorBindingCount <= 0 ||
			recordingState.DescriptorBindings is not { } descriptorBindings ||
			descriptorBindings.Length < recordingState.DescriptorBindingCount)
		{
			return false;
		}

		bool transitionedPublishedSet = false;
		for (int bindingIndex = 0;
			 bindingIndex < recordingState.DescriptorBindingCount;
			 bindingIndex++)
		{
			DescriptorSet set = descriptorBindings[bindingIndex].DescriptorSet;
			if (set.Handle != 0)
				transitionedPublishedSet |=
					CommandOperations.TransitionPublishedDescriptorSetImagesForSampling(
						commandBuffer,
						set,
						target,
						passIndex,
						passMetadata);
		}

		return transitionedPublishedSet;
	}

	internal bool TryTransitionPreparedDescriptorImagesForSampling(
		CommandBuffer commandBuffer,
		in PendingMeshDraw draw,
		int drawUniformSlot,
		int frameIndex,
		XRFrameBuffer? target,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		bool frameDataAlreadyPrewarmed = false)
	{
		lock (_recordDrawSync)
		{
			if ((!frameDataAlreadyPrewarmed &&
				 !TryPrewarmFrameDataForRecordingNoLock(
					draw,
					drawUniformSlot,
					frameIndex,
					out _)) ||
				BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap ||
				_descriptorSets is not { Length: > 0 })
			{
				return false;
			}

			int descriptorSlotIndex = ResolveDescriptorFrameIndex(frameIndex, _descriptorSets.Length);
			DescriptorSet[] sets = _descriptorSets[descriptorSlotIndex];
			bool resolvedPublishedSet = false;
			for (int setIndex = 0; setIndex < sets.Length; setIndex++)
			{
				DescriptorSet set = sets[setIndex];
				if (set.Handle != 0)
					resolvedPublishedSet |= CommandOperations.TransitionPublishedDescriptorSetImagesForSampling(
						commandBuffer,
						set,
						target,
						passIndex,
						passMetadata);
			}

			return resolvedPublishedSet;
		}
	}

	private bool TryPrewarmFrameDataForRecordingNoLock(
		in PendingMeshDraw draw,
		int drawUniformSlot,
		int frameIndex,
		out string reason)
	{
		reason = "Ready";
		XRMaterial material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);

		if (!IsActive)
			Generate();

		if (!IsActive)
		{
			reason = "inactive";
			return false;
		}

		if (Data is null)
		{
			reason = "mesh data missing";
			return false;
		}

		if (ReferenceEquals(material, null))
		{
			reason = "material missing";
			return false;
		}

		if (!ReferenceEquals(_lastPreparedMaterial, material))
		{
			_pipelineDirty = true;
			_descriptorDirty = true;
			_lastPreparedMaterial = material;
		}

		if (MeshRenderer.HasRenderDataPreparation)
			MeshRenderer.OnPreparingRenderData();

		if (draw.PreparedProgram is { } preparedProgram)
		{
			if (!ActivateCapturedProgram(material, preparedProgram, draw.PreparedProgramIdentity, draw.PreparedProgramLinkGeneration))
			{
				reason = "program pending";
				return false;
			}
		}
		else if (!EnsureProgram(material))
		{
			reason = "program pending";
			return false;
		}

		EnsureRuntimeDeformationBuffersCurrent();
		bool usesShaderGeneratedVertices = ProgramUsesShaderGeneratedVertices();
		EnsureBuffers(usesShaderGeneratedVertices);

		if (!AreCachedBuffersReadyForRendering(out string bufferDetail, usesShaderGeneratedVertices))
		{
			reason = $"buffers not ready: {bufferDetail}";
			return false;
		}

		ApplyScopedProgramBindingsForPreparation(material);
		if (draw.ProgramBindingSnapshot is { } programBindingSnapshot)
			_program?.ApplyBindingSnapshot(programBindingSnapshot);
		if (_program?.Data is { } programData)
			NotifyDrawUniforms(material, programData, draw);

		BuildVertexInputState();

		if (!EnsureDescriptorSets(material, drawUniformSlot, frameIndex, draw.ProgramBindingSnapshot))
		{
			reason = $"descriptors pending ({_lastDescriptorPreparationFailure}); program='{_program?.Data?.Name ?? "<unnamed program>"}'";
			return false;
		}

		if (!TryRefreshFrameSourceDescriptorSetsForDraw(
				frameIndex,
				drawUniformSlot,
				material,
				draw.ProgramBindingSnapshot,
				default,
				out string frameSourceReason))
		{
			reason = $"frame-source descriptors pending: {frameSourceReason}";
			return false;
		}

		return true;
	}

	internal bool TryPrewarmGraphicsPipelinesForRecording(
		in PendingMeshDraw draw,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		bool depthStencilReadOnly,
		string pipelineName,
		out string reason)
	{
		lock (_recordDrawSync)
		{
			XRMaterial material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
			bool triangleIndexed = (_triangleIndexBuffer?.BufferHandle?.Handle ?? 0UL) != 0UL;
			bool lineIndexed = (_lineIndexBuffer?.BufferHandle?.Handle ?? 0UL) != 0UL;
			bool pointIndexed = (_pointIndexBuffer?.BufferHandle?.Handle ?? 0UL) != 0UL;
			bool triangleOnly = MeshRenderMaterialResolver.RequiresTriangleOnlyDrawsForCurrentPass();
			bool anyIndexed = triangleIndexed || (!triangleOnly && (lineIndexed || pointIndexed));
			bool ready = true;

			if (triangleIndexed)
			{
				ready &= EnsurePipeline(
					material,
					PrimitiveTopology.TriangleList,
					draw,
					renderPass,
					useDynamicRendering,
					dynamicRenderingFormats,
					passIndex,
					passMetadata,
					depthStencilReadOnly,
					pipelineName,
					allowPipelineCreation: true,
					out _);
			}

			if (!triangleOnly && lineIndexed)
			{
				ready &= EnsurePipeline(
					material,
					PrimitiveTopology.LineList,
					draw,
					renderPass,
					useDynamicRendering,
					dynamicRenderingFormats,
					passIndex,
					passMetadata,
					depthStencilReadOnly,
					pipelineName,
					allowPipelineCreation: true,
					out _);
			}

			if (!triangleOnly && pointIndexed)
			{
				ready &= EnsurePipeline(
					material,
					PrimitiveTopology.PointList,
					draw,
					renderPass,
					useDynamicRendering,
					dynamicRenderingFormats,
					passIndex,
					passMetadata,
					depthStencilReadOnly,
					pipelineName,
					allowPipelineCreation: true,
					out _);
			}

			if (!anyIndexed && Mesh is not null && Mesh.VertexCount > 0)
			{
				PrimitiveTopology fallbackTopology = Mesh.Type switch
				{
					EPrimitiveType.Points => PrimitiveTopology.PointList,
					EPrimitiveType.Lines => PrimitiveTopology.LineList,
					EPrimitiveType.LineStrip => PrimitiveTopology.LineStrip,
					EPrimitiveType.TriangleStrip => PrimitiveTopology.TriangleStrip,
					EPrimitiveType.TriangleFan => PrimitiveTopology.TriangleFan,
					EPrimitiveType.Patches => PrimitiveTopology.PatchList,
					_ => PrimitiveTopology.TriangleList,
				};
				if (!triangleOnly || IsTriangleClassTopology(fallbackTopology))
				{
					ready &= EnsurePipeline(
						material,
						fallbackTopology,
						draw,
						renderPass,
						useDynamicRendering,
						dynamicRenderingFormats,
						passIndex,
						passMetadata,
						depthStencilReadOnly,
						pipelineName,
						allowPipelineCreation: true,
						out _);
				}
			}

			reason = ready ? "Ready" : "pipeline compile queued or pending";
			return ready;
		}
	}

	internal bool TryRefreshReusableCommandBufferFrameData(
		uint imageIndex,
		in PendingMeshDraw draw,
		int drawUniformSlot,
		out string reason,
		bool refreshMaterialUniforms = true,
		bool descriptorResourcesCapturedByFrameSignature = false,
		CommandBuffer recordedSecondaryCommandBuffer = default)
	{
		// Reuse does not record Vulkan commands, but it does update the same
		// renderer-owned descriptor and UBO state consumed by RecordDraw.
		// Serializing it with recording keeps a draw from observing another
		// output's active program or dynamic-uniform slot during camera motion.
		lock (_recordDrawSync)
			return TryRefreshReusableCommandBufferFrameDataNoLock(
				imageIndex,
				draw,
				drawUniformSlot,
				out reason,
				refreshMaterialUniforms,
				descriptorResourcesCapturedByFrameSignature,
				recordedSecondaryCommandBuffer);
	}

	internal bool TryRefreshReusableFrequencyData(
		uint imageIndex,
		in PendingMeshDraw draw,
		int drawUniformSlot,
		EVulkanBindingFrequencyMask frequencyMask,
		out string reason)
	{
		lock (_recordDrawSync)
		{
			reason = "frame-frequency reusable";
			XRMaterial material =
				draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
			if (draw.PreparedProgram is not { } preparedProgram ||
				!ActivateCapturedProgram(
					material,
					preparedProgram,
					draw.PreparedProgramIdentity,
					draw.PreparedProgramLinkGeneration))
			{
				reason = "captured program pending";
				return false;
			}

			if (BackendContext.Resources.MappedFrameArena is null ||
				BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap ||
				_program is null ||
				!ReferenceEquals(_program, draw.PreparedProgram))
			{
				reason = "owner-only refresh eligibility changed";
				return false;
			}

			int frameIndex = unchecked(
				(int)Math.Min(imageIndex, int.MaxValue));
			using VulkanCpuStageScope autoUniformStage =
				default;
			UpdateAutoUniformBuffersForDraw(
				frameIndex,
				drawUniformSlot,
				material,
				draw,
				frequencyMask);
			return true;
		}
	}

	internal bool SupportsOwnerOnlyReusableFrameDataRefresh(
		in PendingMeshDraw draw)
	{
		lock (_recordDrawSync)
			return SupportsOwnerOnlyReusableFrameDataRefreshNoLock(
				draw,
				allowPlanOwnedFrameSourceSamplers: false);
	}

	internal bool SupportsOwnerOnlyReusableFrameDataRefresh(
		in PendingMeshDraw draw,
		bool allowPlanOwnedFrameSourceSamplers)
	{
		lock (_recordDrawSync)
			return SupportsOwnerOnlyReusableFrameDataRefreshNoLock(
				draw,
				allowPlanOwnedFrameSourceSamplers);
	}

	internal string? DescribeOwnerOnlyReusableFrameDataRefreshBlocker(
		in PendingMeshDraw draw)
	{
		lock (_recordDrawSync)
			return DescribeOwnerOnlyReusableFrameDataRefreshBlockerNoLock(draw);
	}

	private bool SupportsOwnerOnlyReusableFrameDataRefreshNoLock(
		in PendingMeshDraw draw,
		bool allowPlanOwnedFrameSourceSamplers)
		=> DescribeOwnerOnlyReusableFrameDataRefreshBlockerNoLock(
			draw,
			allowPlanOwnedFrameSourceSamplers) is null;

	private string? DescribeOwnerOnlyReusableFrameDataRefreshBlockerNoLock(
		in PendingMeshDraw draw,
		bool allowPlanOwnedFrameSourceSamplers = false)
	{
		if (BackendContext.Resources.MappedFrameArena is null)
			return "mesh frame-data arena disabled";
		if (BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
			return "descriptor-heap draw binding";
		VkRenderProgram? publicationProgram =
			draw.PreparedProgram ?? _program;
		if (publicationProgram is null)
			return "prepared program unavailable";
		if (draw.PreparedProgram is not null &&
			draw.PreparedProgramLinkGeneration !=
				draw.PreparedProgram.LinkGeneration)
			return "prepared program link generation changed";
		XRMaterial? material =
			draw.MaterialOverride ?? MeshRenderer.Material;
		bool hasLegacyUniformCallback =
			MeshRenderer.CaptureUniformsOnRender ||
			MeshRenderer.HasSettingUniformsHandlers ||
			material?.HasSettingUniformsHandlers == true;
		if (hasLegacyUniformCallback)
		{
			if (draw.ProgramBindingSnapshot is null)
				return "mutable legacy callback writes were not captured";
		}
		if (draw.ProgramBindingSnapshot is { } bindingSnapshot &&
			!bindingSnapshot.HasPublishedBindingLayoutSignatures)
			return "prepared binding signatures are unpublished";
		if (!allowPlanOwnedFrameSourceSamplers &&
			draw.ProgramBindingSnapshot?.HasMutableFrameSourceSamplerBindings == true)
		{
			// Owner-only refresh intentionally visits frequency-owned UBO work and
			// skips the draw's descriptor publication. Frame sources such as the
			// final-present SourceTexture can replace their physical image while the
			// draw and its recorded secondary remain reusable, so they must retain a
			// per-draw fallback that republishes the current descriptor for this slot.
			return "mutable frame-source samplers require per-draw descriptor refresh";
		}
		if (_engineUniformBuffers.Count != 0)
		{
			foreach (string engineUniformName in
					 _engineUniformBuffers.Keys)
			{
				// The unresolved-descriptor fallback is zero-initialized during
				// full publication and has no frame-varying content. Its exact
				// descriptor generation is already part of the reusable batch
				// signature, so stable reuse does not need to clear it again.
				if (string.Equals(
						engineUniformName,
						FallbackDescriptorUniformName,
						StringComparison.Ordinal))
				{
					continue;
				}

				return $"legacy engine uniform buffer '{engineUniformName}' is active";
			}
		}
		if (Mesh?.HasSkinning == true)
			return "skinning frame-source descriptors are active";
		if (Mesh?.HasBlendshapes == true)
			return "blendshape frame-source descriptors are active";

		foreach (AutoUniformBlockInfo block in
				 publicationProgram.AutoUniformBlockMap.Values)
		{
			if (block.Frequency == EVulkanBindingFrequency.Unknown)
				return $"auto-uniform block '{block.InstanceName}' has unknown frequency";
		}

		return null;
	}

	private bool TryRefreshReusableCommandBufferFrameDataNoLock(
		uint imageIndex,
		in PendingMeshDraw draw,
		int drawUniformSlot,
		out string reason,
		bool refreshMaterialUniforms,
		bool descriptorResourcesCapturedByFrameSignature,
		CommandBuffer recordedSecondaryCommandBuffer)
	{
		reason = "reusable";
		XRMaterial material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
		if (draw.PreparedProgram is { } preparedProgram)
		{
			if (!ActivateCapturedProgram(material, preparedProgram, draw.PreparedProgramIdentity, draw.PreparedProgramLinkGeneration))
			{
				reason = "captured program pending";
				return false;
			}

			EnsureRuntimeDeformationBuffersCurrent();
		}
		else if (!TryPrepareForRendering(material, out string prepareReason))
		{
			reason = $"prepare:{prepareReason}; {LastPrepareDetail}";
			return false;
		}

		if (!IsActive)
		{
			reason = "inactive";
			return false;
		}

		if (Data is null)
		{
			reason = "mesh data missing";
			return false;
		}

		if (_program is null)
		{
			reason = "program missing";
			return false;
		}

		if (_buffersDirty)
		{
			reason = "buffers dirty";
			return false;
		}

		if (!AreCachedBuffersReadyForRendering(out string bufferDetail, ProgramUsesShaderGeneratedVertices()))
		{
			reason = $"buffers not ready: {bufferDetail}";
			return false;
		}

		if (refreshMaterialUniforms &&
			draw.ProgramBindingSnapshot is null &&
			_program is { } activeProgram)
		{
			// Keep snapshot-less material callbacks and all of their consumers in one
			// private draw scope. This avoids publishing transient draw bindings through
			// the program-wide dictionaries and then reacquiring that monitor for every
			// auto-uniform member.
			using VkRenderProgram.BindingUpdateScope bindingUpdate = activeProgram.BeginBindingUpdate();
			return TryRefreshReusableCommandBufferFrameDataBindingsNoLock(
				imageIndex,
				draw,
				drawUniformSlot,
				out reason,
				refreshMaterialUniforms,
				material,
				descriptorResourcesCapturedByFrameSignature,
				recordedSecondaryCommandBuffer);
		}

		return TryRefreshReusableCommandBufferFrameDataBindingsNoLock(
			imageIndex,
			draw,
			drawUniformSlot,
			out reason,
			refreshMaterialUniforms,
			material,
			descriptorResourcesCapturedByFrameSignature,
			recordedSecondaryCommandBuffer);
	}

	private bool TryRefreshReusableCommandBufferFrameDataBindingsNoLock(
		uint imageIndex,
		in PendingMeshDraw draw,
		int drawUniformSlot,
		out string reason,
		bool refreshMaterialUniforms,
		XRMaterial material,
		bool descriptorResourcesCapturedByFrameSignature,
		CommandBuffer recordedSecondaryCommandBuffer)
	{
		reason = "reusable";
		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameDataDrawVisited();
        if (refreshMaterialUniforms &&
            draw.ProgramBindingSnapshot is null &&
            _program?.Data is { } programData)
            NotifyDrawUniforms(material, programData, draw);

		int frameIndex = unchecked((int)Math.Min(imageIndex, int.MaxValue));
		bool capturedDescriptorResources =
			descriptorResourcesCapturedByFrameSignature ||
			draw.ProgramBindingSnapshot is not null;
        using (VulkanCpuStageScope descriptorStage =
				default)
        {
            bool descriptorSetsReusable = CanReuseRecordedDescriptorSets(
                material,
                drawUniformSlot,
                capturedDescriptorResources,
                frameIndex,
                draw.ProgramBindingSnapshot,
                out string descriptorReason);
            if (!descriptorSetsReusable)
            {
                reason = $"descriptors {descriptorReason}; snapshot={(draw.ProgramBindingSnapshot is null ? "none" : "captured")} program='{_program?.Data?.Name ?? "<unnamed program>"}'";
                return false;
            }
			bool hasDescriptorBindings =
				_program?.DescriptorSetLayouts.Count > 0 &&
				_program.DescriptorBindings.Count > 0;
			if (recordedSecondaryCommandBuffer.Handle != 0 &&
				hasDescriptorBindings &&
				!ActiveDescriptorSetsMatchRecordedCommandBuffer(
					frameIndex,
					recordedSecondaryCommandBuffer,
					out string recordedDescriptorReason))
			{
				reason = $"descriptors {recordedDescriptorReason}; program='{_program?.Data?.Name ?? "<unnamed program>"}'";
				return false;
			}
            if (!TryRefreshSharedMaterialDescriptorSetForReusableFrame(
                    material,
                    frameIndex,
                    capturedDescriptorResources,
                    out string sharedMaterialDescriptorReason))
            {
                reason = $"descriptors {sharedMaterialDescriptorReason}; snapshot={(draw.ProgramBindingSnapshot is null ? "none" : "captured")} program='{_program?.Data?.Name ?? "<unnamed program>"}'";
                return false;
            }
            bool frameSourceDescriptorsReady = TryRefreshFrameSourceDescriptorSetsForDraw(
                frameIndex,
                drawUniformSlot,
                material,
                draw.ProgramBindingSnapshot,
				recordedSecondaryCommandBuffer,
                out string frameSourceDescriptorReason);
            if (!frameSourceDescriptorsReady)
            {
                reason = $"descriptors {frameSourceDescriptorReason}; snapshot={(draw.ProgramBindingSnapshot is null ? "none" : "captured")} program='{_program?.Data?.Name ?? "<unnamed program>"}'";
                return false;
            }
        }

        using (VulkanCpuStageScope engineUniformStage =
				default)
        {
            UpdateEngineUniformBuffersForDraw(frameIndex, drawUniformSlot, draw);
        }
        if (refreshMaterialUniforms)
        {
            using VulkanCpuStageScope autoUniformStage =
				default;
            UpdateAutoUniformBuffersForDraw(frameIndex, drawUniformSlot, material, draw);
        }
        return true;
	}

	private bool ActiveDescriptorSetsMatchRecordedCommandBuffer(
		int frameIndex,
		CommandBuffer recordedSecondaryCommandBuffer,
		out string reason)
	{
		if (_descriptorSets is not { Length: > 0 })
		{
			reason = "active descriptor set array is unavailable";
			return false;
		}

		int descriptorSlotIndex = ResolveDescriptorFrameIndex(
			frameIndex,
			_descriptorSets.Length);
		DescriptorSet[] frameSets = _descriptorSets[descriptorSlotIndex];
		if (CommandOperations.CommandBufferReferencesAllDescriptorSets(
				recordedSecondaryCommandBuffer,
				frameSets,
				out ulong missingDescriptorSetHandle))
		{
			reason = "recorded descriptor sets match";
			return true;
		}

		reason =
			$"recorded secondary 0x{recordedSecondaryCommandBuffer.Handle:X} does not bind active descriptor set 0x{missingDescriptorSetHandle:X}";
		return false;
	}

	internal string DescribeReusableCommandBufferFrameDataBlocker(in PendingMeshDraw draw, int drawUniformSlot)
	{
		XRMaterial material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
		if (!TryPrepareForRendering(material, out string prepareReason))
			return $"render preparation failed: {prepareReason}; {LastPrepareDetail}";

		return CanReuseRecordedDescriptorSets(material, drawUniformSlot, out string reason)
			? "reusable descriptor sets; refresh likely failed after descriptor check"
			: reason;
	}

    private void PushPerDrawConstants(CommandBuffer commandBuffer, XRMaterial material, in PendingMeshDraw draw)
	{
		if (_program is null)
			return;

		MeshDrawPushConstants constants = CreatePerDrawPushConstants(material, draw);
		CommandOperations.PushConstantsTracked(
			commandBuffer,
			_program.PipelineLayout,
			VulkanMeshRenderingConventions.CommonPushConstantStageFlags,
			0,
			constants);
	}

	private static MeshDrawPushConstants CreatePerDrawPushConstants(XRMaterial material, in PendingMeshDraw draw)
	{
		uint debugFlags = 0;
		if (draw.IsStereoPass)
			debugFlags |= 1u;
		if (draw.UseUnjitteredProjection)
			debugFlags |= 2u;

		return new MeshDrawPushConstants(
			unchecked((uint)(material.GetHashCode() & int.MaxValue)),
			draw.Instances,
			(uint)draw.BillboardMode,
			debugFlags);
	}

	/// <summary>
	/// Computes a hash of the vertex input layout (bindings + attributes) for
	/// use as part of the pipeline cache key.
	/// </summary>
	private ulong ComputeVertexLayoutHash()
	{
		VulkanStableHash64 hash = new(schemaVersion: 2);
		hash.Add(_geometryLayoutSignature.StableHash);
		hash.Add(_geometryLayoutSignature.VertexBufferCount);
		hash.Add(_geometryLayoutSignature.VertexAttributeCount);
		hash.Add(_vertexBindings.Length);
		hash.Add(_vertexAttributes.Length);

		for (int i = 0; i < _vertexBindings.Length; i++)
		{
			hash.Add(_vertexBindings[i].Binding);
			hash.Add(_vertexBindings[i].Stride);
			hash.Add((int)_vertexBindings[i].InputRate);
		}

		for (int i = 0; i < _vertexAttributes.Length; i++)
		{
			hash.Add(_vertexAttributes[i].Location);
			hash.Add(_vertexAttributes[i].Binding);
			hash.Add((int)_vertexAttributes[i].Format);
			hash.Add(_vertexAttributes[i].Offset);
		}

		return hash.Value;
	}

	/// <summary>
	/// Maps a command buffer handle back to its swapchain image index.
	/// Returns -1 if the buffer is not in the current set (e.g. transient buffer).
	/// </summary>
	private int ResolveCommandBufferIndex(CommandBuffer commandBuffer)
		=> CommandOperations.ResolveCommandBufferImageIndex(commandBuffer);
}
