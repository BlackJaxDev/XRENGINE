// Records indexed and non-indexed mesh draws into Vulkan command buffers.

using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.UI;

using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	private readonly Lock _recordDrawSync = new();
	private readonly string _drawSkipWarningKey = $"Vulkan.MeshRenderer.DrawSkip.{Interlocked.Increment(ref _nextDrawSkipWarningId)}";
	private static long _nextDrawSkipWarningId;

	private void ReportSkippedDraw(string reason, int passIndex, string targetName)
	{
		Debug.VulkanWarningEvery(
			_drawSkipWarningKey,
			TimeSpan.FromSeconds(2),
			"[Vulkan] Mesh draw skipped. mesh='{0}' pass={1} target='{2}' reason={3}",
			Mesh?.Name ?? "<unnamed mesh>", passIndex, targetName, reason);
	}

	private static IDisposable? StartMeshDrawDetailScope(string name)
		=> VulkanMeshRenderingConventions.CommandRecordingDetailProfilingEnabled
			? RuntimeRenderingHostServices.Profiling.StartProfileScope(name)
			: null;

	/// <summary>
	/// Records draw commands into the given Vulkan command buffer for all
	/// primitive types available on the mesh (triangles, lines, points).
	/// Falls back to non-indexed drawing if no indexed draw succeeds.
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
		XRFrameBuffer? target,
		in FrameOpContext context,
		bool depthStencilReadOnly,
		string pipelineName,
		string targetName,
		int drawUniformSlot,
		int frameDataImageIndex)
		=> RecordDraw(commandBuffer, draw, renderPass, useDynamicRendering,
			dynamicRenderingFormats, passIndex, passMetadata, target, context,
			depthStencilReadOnly, pipelineName, targetName, drawUniformSlot,
			frameDataImageIndex, out _, out _);

	/// <summary>
	/// Records a draw and returns its failure under the recording lock.
	/// An empty failure reason permits an intentional draw omission.
	/// </summary>
	internal bool RecordDraw(
		CommandBuffer commandBuffer,
		in PendingMeshDraw draw,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		XRFrameBuffer? target,
		in FrameOpContext context,
		bool depthStencilReadOnly,
		string pipelineName,
		string targetName,
		int drawUniformSlot,
		int frameDataImageIndex,
		out string failureReason,
		out bool pipelineUnavailable,
		VulkanWorldUiNativeProof? worldUiProof = null,
		VulkanAcceptedFramePlan? worldUiPlan = null,
		ulong worldUiNativeToken = 0UL)
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
				target,
				context,
				depthStencilReadOnly,
				pipelineName,
				targetName,
				drawUniformSlot,
				frameDataImageIndex,
				out failureReason,
				out pipelineUnavailable,
				worldUiProof,
				worldUiPlan,
				worldUiNativeToken);
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
		XRFrameBuffer? target,
		in FrameOpContext context,
		bool depthStencilReadOnly,
		string pipelineName,
		string targetName,
		int drawUniformSlot,
		int frameDataImageIndex,
		out string failureReason,
		out bool pipelineUnavailable,
		VulkanWorldUiNativeProof? worldUiProof,
		VulkanAcceptedFramePlan? worldUiPlan,
		ulong worldUiNativeToken)
	{
		failureReason = string.Empty;
		pipelineUnavailable = false;
		bool worldProofRequired = worldUiPlan?.RequiresWorldUiOutput == true ||
			draw.WorldUiCommand is not null ||
			worldUiProof?.RequiresNativeDrawProof(target) == true;
		if (worldProofRequired &&
			(worldUiProof is null || worldUiPlan is null ||
			 !worldUiProof.IsBoundToPlan(worldUiPlan,
				 worldUiNativeToken)))
		{
			failureReason = "retained world UI indexed draw has no accepted native proof";
			return false;
		}
		ActivateDrawMaterializationSnapshot(draw);
		var material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
		if (!TryPrepareDrawForRecording(draw, material, drawUniformSlot, frameDataImageIndex, out string prepareReason))
		{
			failureReason = $"preparation failed: {prepareReason}; {LastPrepareDetail}";
			ReportSkippedDraw(failureReason, passIndex, targetName);
			if (XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw)
				Debug.MeshesWarning("[DrawTrace] {0}: skipped before command recording because preparation failed: {1} {2}", Mesh?.Name ?? "?", prepareReason, LastPrepareDetail);
			return false;
		}

		bool skipLinePointDraws = MeshRenderMaterialResolver.RequiresTriangleOnlyDrawsForCurrentPass();

		// The recording lock protects mutable program and material bindings.
		// Pass draw inputs and failure state by reference inside that lock.
		DrawRecordingState state = new()
		{
			CommandBuffer = commandBuffer,
			Draw = draw,
			Target = target,
			WorldUiProof = worldUiProof,
			WorldUiPlan = worldUiPlan,
			WorldUiNativeToken = worldUiNativeToken,
			WorldUiProofRequired = worldProofRequired,
			Material = material,
			RenderPass = renderPass,
			UseDynamicRendering = useDynamicRendering,
			DynamicRenderingFormats = dynamicRenderingFormats,
			PassIndex = passIndex,
			PassMetadata = passMetadata,
			DepthStencilReadOnly = depthStencilReadOnly,
			PipelineName = pipelineName,
			TargetName = targetName,
			DrawUniformSlot = drawUniformSlot,
			FrameDataImageIndex = frameDataImageIndex,
			VerboseTrace = (useDynamicRendering && renderPass.Handle == 0 && XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw)
				|| XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw,
		};
		UploadDrawDeformationData();

		// Draw each available primitive type. Do not stop after the first success:
		// one mesh can contain triangles, lines, and points.
		bool drew = false;
		if (_triangleIndexBuffer?.BufferHandle is { } triHandle && triHandle.Handle != 0)
			drew |= TryRecordIndexedDraw(ref state, _triangleIndexBuffer, _triangleIndexSize, PrimitiveTopology.TriangleList);
		if (!skipLinePointDraws)
		{
			if (_lineIndexBuffer?.BufferHandle is { } lineHandle && lineHandle.Handle != 0)
				drew |= TryRecordIndexedDraw(ref state, _lineIndexBuffer, _lineIndexSize, PrimitiveTopology.LineList);
			if (_pointIndexBuffer?.BufferHandle is { } pointHandle && pointHandle.Handle != 0)
				drew |= TryRecordIndexedDraw(ref state, _pointIndexBuffer, _pointIndexSize, PrimitiveTopology.PointList);
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

		// Use the non-indexed fallback if no indexed draw succeeds.
		if (!drew && Mesh is not null && !worldProofRequired)
			drew = TryRecordNonIndexedDraw(ref state, skipLinePointDraws);

		// An intentional omission has an empty reason. Any successful path clears
		// earlier indexed failures from the result of this logical draw.
		if (!drew)
		{
			if (worldProofRequired)
				state.FailureReason ??= "retained world UI indexed command did not issue";
			failureReason = state.FailureReason ?? string.Empty;
			pipelineUnavailable = state.PipelineFailureObserved;
			if (failureReason.Length > 0)
				ReportSkippedDraw(failureReason, passIndex, targetName);
		}
		return drew;
	}

	private bool TryPrepareDrawForRecording(in PendingMeshDraw draw, XRMaterial material, int drawUniformSlot, int frameDataImageIndex, out string reason)
	{
		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.Prepare"))
		{
			return draw.PreparedProgram is { } preparedProgram
				? TryPrepareCapturedProgramForRecording(
					material, preparedProgram, draw.PreparedProgramIdentity,
					draw.PreparedProgramLinkGeneration, draw.ProgramBindingSnapshot,
					drawUniformSlot, frameDataImageIndex, out reason)
				: TryPrepareForRendering(material, out reason);
		}
	}

	private void UploadDrawDeformationData()
	{
		// Upload deformation data before recording commands that use it.
		// Static meshes avoid the shared invalidation checks.
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
	}

	/// <summary>
	/// Records one indexed primitive type. The caller tries all available types.
	/// A later successful type can leave an earlier failure without failing the draw.
	/// </summary>
	private bool TryRecordIndexedDraw(ref DrawRecordingState state, VkDataBuffer? indexBuffer, IndexSize size, PrimitiveTopology topology)
	{
		if (indexBuffer?.BufferHandle is not { } indexHandle)
		{
			if (state.VerboseTrace)
				Debug.MeshesWarning("[DrawTrace] {0}: no indexBuffer for {1}", Mesh?.Name ?? "?", topology);
			return false;
		}

		if (size == IndexSize.Byte && !BackendContext.Supports(EVulkanDeviceCapability.IndexTypeUint8))
		{
			state.FailureReason = "Vulkan indexTypeUint8 is unavailable";
			WarnOnce("Skipping indexed draw using byte-sized indices because Vulkan indexTypeUint8 is not enabled.");
			return false;
		}

		uint indexCount = indexBuffer.Data.ElementCount;
		if (indexCount == 0)
		{
			if (state.VerboseTrace)
				Debug.MeshesWarning("[DrawTrace] {0}: indexCount=0 for {1}", Mesh?.Name ?? "?", topology);
			return false;
		}

		Pipeline pipeline = default;
		bool pipelineReady;
		string pipelineFailure;
		// Pipelines are prepared before command recording. Do not create one here
		// or wait for foreground work while the command buffer is being built.
		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.EnsurePipeline"))
		{
			pipelineReady = EnsurePipelineCore(
				state.Material, topology, state.Draw, state.RenderPass,
				state.UseDynamicRendering, state.DynamicRenderingFormats,
				state.PassIndex, state.PassMetadata, state.DepthStencilReadOnly,
				state.PipelineName, allowPipelineCreation: false, foregroundRequired: false,
				out pipeline, out _, out pipelineFailure);
		}
		if (!pipelineReady)
		{
			state.PipelineFailureObserved = true;
			state.FailureReason = $"indexed pipeline unavailable: {pipelineFailure}";
			if (state.VerboseTrace)
				Debug.MeshesWarning("[DrawTrace] {0}: EnsurePipeline FAILED for {1} dynRender={2} colors={3} depthFmt={4}",
					Mesh?.Name ?? "?", topology, state.UseDynamicRendering, state.DynamicRenderingFormats.DescribeColorFormats(), state.DynamicRenderingFormats.DepthAttachmentFormat);
			return false;
		}

		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindPipeline"))
			CommandOperations.BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Graphics, pipeline);

		bool vertexBuffersBound;
		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindVertexBuffers"))
			vertexBuffersBound = BindVertexBuffersForCurrentPipeline(state.CommandBuffer);
		if (!vertexBuffersBound)
		{
			state.FailureReason = "indexed vertex buffers unavailable";
			if (state.VerboseTrace)
				Debug.MeshesWarning("[DrawTrace] {0}: BindVertexBuffers FAILED", Mesh?.Name ?? "?");
			return false;
		}

		// One logical draw can record several primitive types. Notify its uniforms once.
		if (!state.UniformsNotified && _program?.Data is { } programData)
		{
			using (StartMeshDrawDetailScope("Vulkan.MeshDraw.NotifyUniforms"))
				NotifyDrawUniforms(state.Material, programData, state.Draw);
			state.UniformsNotified = true;
		}

		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.PushConstants"))
			PushPerDrawConstants(state.CommandBuffer, state.Material, state.Draw);

		bool descriptorsBound;
		VulkanWorldUiDescriptorBindingWitness worldDescriptorWitness = default;
		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindDescriptors"))
			descriptorsBound = BindDescriptorsIfAvailable(state.CommandBuffer,
				state.Material, state.Draw, state.DrawUniformSlot,
				state.FrameDataImageIndex, state.PassIndex, out _,
				state.WorldUiProofRequired, out worldDescriptorWitness);
		if (!descriptorsBound)
		{
			state.FailureReason = "indexed descriptors unavailable";
			if (state.VerboseTrace)
				Debug.MeshesWarning("[DrawTrace] {0}: BindDescriptors FAILED", Mesh?.Name ?? "?");
			return false;
		}

		if (state.VerboseTrace)
			TraceIndexedDraw(state, pipeline, topology, indexCount);

		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.CmdDrawIndexed"))
		{
			CommandOperations.BindIndexBufferTracked(state.CommandBuffer, indexHandle, 0, ToVkIndexType(size));
			VulkanProgramInterfaceEntry? retainedWorldInterface = null;
			try
			{
				if (state.Draw.WorldUiCommand is { } worldUiCommand &&
					(!HasExactWorldUiCropProgram(state.Draw,
						worldUiCommand, out retainedWorldInterface) ||
					 !ForceWorldUiDescriptorSetsAtIssue(
						 state.CommandBuffer, in worldDescriptorWitness,
						 state.Material)))
				{
					state.FailureReason = "retained world UI compiled shader or descriptor owner changed";
					return false;
				}
				if (state.WorldUiProofRequired &&
					state.Draw.WorldUiCommand is null &&
					(!HasExactWorldUiFactoryProgram(state.Draw,
						state.Material, out retainedWorldInterface) ||
					 !ForceWorldUiDescriptorSetsAtIssue(
						 state.CommandBuffer, in worldDescriptorWitness,
						 state.Material)))
				{
					state.FailureReason = "world UI output stage compiled shader or descriptor owner changed";
					return false;
				}
				VulkanWorldUiDrawToken worldDrawToken = default;
				bool worldOutputStage = false;
				if (state.Draw.WorldUiCommand is { } worldCommand)
				{
					EPrimitiveType worldTopology = topology switch
					{
						PrimitiveTopology.TriangleList => EPrimitiveType.Triangles,
						PrimitiveTopology.LineList => EPrimitiveType.Lines,
						PrimitiveTopology.PointList => EPrimitiveType.Points,
						_ => (EPrimitiveType)(-1),
					};
					if (state.WorldUiProof is null || state.WorldUiPlan is null ||
						!state.WorldUiProof.TryBeginWorldIndexedDraw(
							state.WorldUiPlan, state.WorldUiNativeToken,
							worldCommand, state.Target, MeshRenderer,
							worldTopology, size, indexCount,
							state.Draw.Instances, out worldDrawToken))
					{
						state.FailureReason = "retained world UI indexed intent changed before native issue";
						return false;
					}
				}
				else if (state.WorldUiProofRequired)
				{
					worldOutputStage = true;
					if (state.WorldUiProof is null ||
						state.WorldUiPlan is null ||
						!state.WorldUiProof.TryBeginStageIndexedDraw(
							state.WorldUiPlan, state.WorldUiNativeToken,
							state.Target, MeshRenderer, state.Material,
							topology, indexCount, state.Draw.Instances,
							out worldDrawToken))
					{
						state.FailureReason = "world UI output stage lacks exact native draw authority";
						return false;
					}
				}
				try
				{
					Api!.CmdDrawIndexed(state.CommandBuffer, indexCount, state.Draw.Instances, 0, 0, 0);
					if (worldDrawToken.Serial != 0UL &&
						!(worldOutputStage
							? state.WorldUiProof!.CompleteStageIndexedDraw(worldDrawToken)
							: state.WorldUiProof!.CompleteWorldIndexedDraw(
								state.Draw.WorldUiCommand!, worldDrawToken)))
					{
						state.FailureReason = "retained world UI indexed issue lost its native owner";
						return false;
					}
				}
				finally
				{
					if (worldDrawToken.Serial != 0UL)
						state.WorldUiProof!.FailWorldIndexedDraw(worldDrawToken);
				}
			_program?.RecordShaderCommandCoverage(ShaderCommandKind.Graphics, indirect: false, instancingKnown: true, instanced: state.Draw.Instances > 1);
			}
			finally
			{
				if (retainedWorldInterface is not null)
					BackendContext.Resources.ProgramInterfaces.Release(
						retainedWorldInterface);
			}
		}

		return true;
	}

	private void TraceIndexedDraw(in DrawRecordingState state, Pipeline pipeline, PrimitiveTopology topology, uint indexCount)
	{
		var draw = state.Draw;
		Debug.MeshesWarning("[DrawTrace] {0}: CmdDrawIndexed({1}) pass={2} target={3} dynRender={4} dsReadOnly={5} pipeline=0x{6:X} topology={7} cull={8} blend={9} blendOps={10}/{11} blendFactors={12},{13},{14},{15} alphaToCoverage={16} depthTest={17} depthWrite={18} depthCmp={19} colorWrite={20} viewport=({21},{22},{23},{24}) scissor=({25},{26},{27},{28}) prog={29}",
			Mesh?.Name ?? "?", indexCount,
			state.PassIndex, state.TargetName, state.UseDynamicRendering, state.DepthStencilReadOnly,
			pipeline.Handle, topology,
			draw.CullMode, draw.BlendEnabled,
			draw.ColorBlendOp, draw.AlphaBlendOp,
			draw.SrcColorBlendFactor, draw.DstColorBlendFactor, draw.SrcAlphaBlendFactor, draw.DstAlphaBlendFactor,
			draw.AlphaToCoverageEnabled,
			draw.DepthTestEnabled, draw.DepthWriteEnabled, draw.DepthCompareOp, draw.ColorWriteMask,
			draw.Viewport.X, draw.Viewport.Y, draw.Viewport.Width, draw.Viewport.Height,
			draw.Scissor.Offset.X, draw.Scissor.Offset.Y, draw.Scissor.Extent.Width, draw.Scissor.Extent.Height,
			_program?.Data?.Name ?? "?prog");
	}

	/// <summary>
	/// Records a non-indexed draw only when no indexed primitive was drawn.
	/// This path uses the mesh topology because no index buffer selects one.
	/// </summary>
	private bool TryRecordNonIndexedDraw(ref DrawRecordingState state, bool skipLinePointDraws)
	{
		var mesh = Mesh!;
		uint vertexCount = (uint)Math.Max(mesh.VertexCount, 0);
		PrimitiveTopology topology = mesh.Type switch
		{
			EPrimitiveType.Points => PrimitiveTopology.PointList,
			EPrimitiveType.Lines => PrimitiveTopology.LineList,
			EPrimitiveType.LineStrip => PrimitiveTopology.LineStrip,
			EPrimitiveType.TriangleStrip => PrimitiveTopology.TriangleStrip,
			EPrimitiveType.TriangleFan => PrimitiveTopology.TriangleFan,
			EPrimitiveType.Patches => PrimitiveTopology.PatchList,
			_ => PrimitiveTopology.TriangleList,
		};

		if (skipLinePointDraws && !IsTriangleClassTopology(topology))
		{
			Debug.VulkanWarningEvery(
				$"Vulkan.MeshRenderer.ShadowTriangleOnlyFallback.{mesh.Name ?? "UnnamedMesh"}",
				TimeSpan.FromSeconds(2),
				"[Vulkan] Suppressed non-indexed {0} fallback for shadow geometry pass. mesh='{1}' layout={2}",
				topology,
				mesh.Name ?? "<unnamed mesh>",
				_geometryLayoutSignature.DebugSummary);
			return false;
		}
		if (vertexCount == 0)
			return false;

		Pipeline pipeline = default;
		bool pipelineReady;
		string pipelineFailure;
		// Use the pipeline prepared before command recording; do not create or
		// wait for foreground pipeline work in this path.
		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.EnsurePipeline"))
		{
			pipelineReady = EnsurePipelineCore(
				state.Material, topology, state.Draw, state.RenderPass,
				state.UseDynamicRendering, state.DynamicRenderingFormats,
				state.PassIndex, state.PassMetadata, state.DepthStencilReadOnly,
				state.PipelineName, allowPipelineCreation: false, foregroundRequired: false,
				out pipeline, out _, out pipelineFailure);
		}
		if (!pipelineReady && state.VerboseTrace)
			Debug.MeshesWarning("[DrawTrace] {0}: non-indexed pipeline unavailable: {1}; pass={2} target={3} dynRender={4} colors={5} depthFmt={6}",
				Mesh?.Name ?? "?", pipelineFailure, state.PassIndex, state.TargetName, state.UseDynamicRendering, state.DynamicRenderingFormats.DescribeColorFormats(), state.DynamicRenderingFormats.DepthAttachmentFormat);
		if (!pipelineReady)
		{
			state.PipelineFailureObserved = true;
			state.FailureReason = $"non-indexed pipeline unavailable: {pipelineFailure}";
			return false;
		}

		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindPipeline"))
			CommandOperations.BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Graphics, pipeline);

		bool vertexBuffersBound;
		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindVertexBuffers"))
			vertexBuffersBound = BindVertexBuffersForCurrentPipeline(state.CommandBuffer);
		if (!vertexBuffersBound)
		{
			state.FailureReason = "non-indexed vertex buffers unavailable";
			if (state.VerboseTrace)
				Debug.MeshesWarning("[DrawTrace] {0}: non-indexed vertex buffers unavailable; pass={1} target={2}", Mesh?.Name ?? "?", state.PassIndex, state.TargetName);
			return false;
		}

		// An indexed attempt may have notified the same logical draw already.
		if (!state.UniformsNotified && _program?.Data is { } programData)
		{
			using (StartMeshDrawDetailScope("Vulkan.MeshDraw.NotifyUniforms"))
				NotifyDrawUniforms(state.Material, programData, state.Draw);
			state.UniformsNotified = true;
		}

		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.PushConstants"))
			PushPerDrawConstants(state.CommandBuffer, state.Material, state.Draw);

		bool descriptorsBound;
		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.BindDescriptors"))
			descriptorsBound = BindDescriptorsIfAvailable(state.CommandBuffer, state.Material, state.Draw, state.DrawUniformSlot, state.FrameDataImageIndex, state.PassIndex);
		if (!descriptorsBound)
		{
			state.FailureReason = "non-indexed descriptors unavailable";
			if (state.VerboseTrace)
				Debug.MeshesWarning("[DrawTrace] {0}: non-indexed descriptors unavailable; pass={1} target={2}", Mesh?.Name ?? "?", state.PassIndex, state.TargetName);
			return false;
		}

		if (state.VerboseTrace)
			TraceNonIndexedDraw(state, pipeline, topology, vertexCount);

		if (VulkanMeshRenderingConventions.BloomVulkanDiagnosticsEnabled && vertexCount <= 6u)
			TraceBloomNonIndexedDraw(state, pipeline, topology, vertexCount);

		using (StartMeshDrawDetailScope("Vulkan.MeshDraw.CmdDraw"))
			Api!.CmdDraw(state.CommandBuffer, vertexCount, state.Draw.Instances, 0, 0);
		_program?.RecordShaderCommandCoverage(ShaderCommandKind.Graphics, indirect: false, instancingKnown: true, instanced: state.Draw.Instances > 1);
		return true;
	}

	private void TraceNonIndexedDraw(in DrawRecordingState state, Pipeline pipeline, PrimitiveTopology topology, uint vertexCount)
	{
		var draw = state.Draw;
		Debug.MeshesWarning("[DrawTrace] {0}: CmdDraw({1}) pass={2} target={3} dynRender={4} dsReadOnly={5} pipeline=0x{6:X} topology={7} cull={8} blend={9} blendOps={10}/{11} blendFactors={12},{13},{14},{15} alphaToCoverage={16} depthTest={17} depthWrite={18} depthCmp={19} colorWrite={20} viewport=({21},{22},{23},{24}) scissor=({25},{26},{27},{28}) prog={29}",
			Mesh?.Name ?? "?", vertexCount,
			state.PassIndex, state.TargetName, state.UseDynamicRendering, state.DepthStencilReadOnly,
			pipeline.Handle, topology,
			draw.CullMode, draw.BlendEnabled,
			draw.ColorBlendOp, draw.AlphaBlendOp,
			draw.SrcColorBlendFactor, draw.DstColorBlendFactor, draw.SrcAlphaBlendFactor, draw.DstAlphaBlendFactor,
			draw.AlphaToCoverageEnabled,
			draw.DepthTestEnabled, draw.DepthWriteEnabled, draw.DepthCompareOp, draw.ColorWriteMask,
			draw.Viewport.X, draw.Viewport.Y, draw.Viewport.Width, draw.Viewport.Height,
			draw.Scissor.Offset.X, draw.Scissor.Offset.Y, draw.Scissor.Extent.Width, draw.Scissor.Extent.Height,
			_program?.Data?.Name ?? "?prog");
	}

	private void TraceBloomNonIndexedDraw(in DrawRecordingState state, Pipeline pipeline, PrimitiveTopology topology, uint vertexCount)
	{
		var draw = state.Draw;
		Debug.VulkanEvery(
			$"Vulkan.BloomDiag.CmdDraw.{state.PassIndex}.{MaterializationSnapshot.ActiveFrameOpContext?.OutputFrameBufferName}.{vertexCount}",
			TimeSpan.FromSeconds(1),
			"[BloomDiag][Vulkan] CmdDraw fullscreen target='{0}' pass={1} vertices={2} pipeline=0x{3:X} topology={4} blend={5} depthTest={6} depthWrite={7} stencil={8} colorWrite={9} viewport=({10},{11},{12},{13}) scissor=({14},{15},{16},{17}) program='{18}' material='{19}'",
			MaterializationSnapshot.ActiveFrameOpContext?.OutputFrameBufferName ?? "<none>",
			state.PassIndex,
			vertexCount,
			pipeline.Handle,
			topology,
			draw.BlendEnabled,
			draw.DepthTestEnabled,
			draw.DepthWriteEnabled,
			draw.StencilTestEnabled,
			draw.ColorWriteMask,
			draw.Viewport.X,
			draw.Viewport.Y,
			draw.Viewport.Width,
			draw.Viewport.Height,
			draw.Scissor.Offset.X,
			draw.Scissor.Offset.Y,
			draw.Scissor.Extent.Width,
			draw.Scissor.Extent.Height,
			_program?.Data?.Name ?? "<program>",
			state.Material.Name ?? "<material>");
	}

	internal bool TryPrepareMeshDrawRecordingState(
		VulkanPreparedFrameRecording preparedFrame,
		int frameIndex,
		in PendingMeshDraw draw,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		XRFrameBuffer? target,
		in FrameOpContext context,
		bool depthStencilReadOnly,
		string pipelineName,
		int drawUniformSlot,
		bool frameDataAlreadyPrewarmed,
		out VulkanPreparedMeshDrawState recordingState,
		out string reason)
	{
		lock (_recordDrawSync)
		{
			return TryPrepareMeshDrawRecordingStateNoLock(
				preparedFrame,
				frameIndex,
				draw,
				renderPass,
				useDynamicRendering,
				dynamicRenderingFormats,
				passIndex,
				passMetadata,
				target,
				context,
				depthStencilReadOnly,
				pipelineName,
				drawUniformSlot,
				frameDataAlreadyPrewarmed,
				out recordingState,
				out reason);
		}
	}

	private bool TryPrepareMeshDrawRecordingStateNoLock(
		VulkanPreparedFrameRecording preparedFrame,
		int frameIndex,
		in PendingMeshDraw draw,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata,
		XRFrameBuffer? target,
		in FrameOpContext context,
		bool depthStencilReadOnly,
		string pipelineName,
		int drawUniformSlot,
		bool frameDataAlreadyPrewarmed,
		out VulkanPreparedMeshDrawState recordingState,
		out string reason)
	{
		recordingState = default;
		ArgumentNullException.ThrowIfNull(preparedFrame);
		frameIndex = Math.Max(frameIndex, 0);
		ActivateDrawMaterializationSnapshot(draw);
		XRMaterial material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
		if (!frameDataAlreadyPrewarmed &&
			!TryPrewarmFrameDataForRecordingNoLock(
				draw,
				drawUniformSlot,
				frameIndex,
				out reason))
		{
			return false;
		}
		if (frameDataAlreadyPrewarmed &&
			!TryReactivatePrewarmedDrawForRecordingNoLock(
				draw,
				material,
				drawUniformSlot,
				frameIndex,
				out reason))
		{
			return false;
		}
		if (_program is not { } program)
		{
			reason = "program missing after mesh draw preparation";
			return false;
		}
		if (draw.CanonicalDrawIdentitySnapshot.IsValid &&
			!preparedFrame.TryRetainCanonicalPublication(
				draw.CanonicalDrawIdentitySnapshot,
				out reason))
		{
			return false;
		}
		VulkanAdvancedScenePublicationState advancedSceneState = default;
		bool advancedSceneReady = false;
		if (draw.CanonicalDrawIdentitySnapshot.IsValid)
		{
			advancedSceneReady =
				preparedFrame.TryPrepareAdvancedScenePublication(
					BackendContext.Resources.AdvancedSceneResources,
					draw.CanonicalDrawIdentitySnapshot,
					out advancedSceneState,
					out EVulkanAdvancedSceneResourceFailure advancedFailure,
					out string advancedReason,
					out bool newlyAttempted);
			if (!advancedSceneReady && newlyAttempted)
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.AdvancedSceneResources.{advancedFailure}",
					TimeSpan.FromSeconds(2),
					"[VulkanAdvancedScene] Native dual-feed projection unavailable ({0}): {1}. The ordered legacy draw remains selected.",
					advancedFailure,
					advancedReason);
			}
		}
		ReleaseCanonicalPublicationBridge(
			draw.CanonicalDrawIdentitySnapshot);
		if (program.ExternallyOwnedDescriptorSetMask != 0u &&
			(!draw.CanonicalDrawIdentitySnapshot.IsValid ||
			 !advancedSceneReady ||
			 !advancedSceneState.IsValid))
		{
			reason =
				"advanced Vulkan program requires a valid retained canonical publication and native descriptor realization";
			return false;
		}

		if (Mesh?.HasSkinning == true)
			MeshRenderer.PushBoneMatricesToGPU();
		if (Mesh?.HasBlendshapes == true)
			MeshRenderer.PushBlendshapeWeightsToGPU();

		VulkanPreparedMeshPrimitive primitive0 = default;
		VulkanPreparedMeshPrimitive primitive1 = default;
		VulkanPreparedMeshPrimitive primitive2 = default;
		int primitiveCount = 0;
		bool residentHit = TryGetResidentDrawTemplate(
			draw,
			passIndex,
			material,
			context,
			out _,
			out VulkanResidentDrawTemplateVariantKey residentVariant,
			out VulkanResidentDrawTemplateGenerationDomains residentGenerations,
			out VulkanResidentDrawTemplate? residentTemplate);
		bool residentLookupAvailable =
			residentGenerations.LayoutTopology != 0 &&
			residentGenerations.Recording != 0;
		VulkanPreparedStreamRange vertexRange;
		if (residentHit && residentTemplate is not null)
		{
			if (!preparedFrame.TryAdoptResidentTemplateUse(
					residentTemplate,
					out reason))
			{
				return false;
			}

			VulkanResidentDrawTemplateNativeState native = residentTemplate.NativeState;
			vertexRange = preparedFrame.AppendVertexBuffers(
				native.VertexBuffers,
				native.VertexBindings);
			primitive0 = native.Primitive0;
			primitive1 = native.Primitive1;
			primitive2 = native.Primitive2;
			primitiveCount = native.PrimitiveCount;
		}
		else if (!TryPrepareVertexBufferSnapshot(
			preparedFrame,
			out vertexRange,
			out reason))
		{
			return false;
		}

		{
			if (!residentHit)
			{
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
			}

			// Prepared recording publishes auto-uniform bytes before descriptor binding.
			// The direct recording path reaches EnsureDescriptorSets first, but this path
			// can reactivate a cached descriptor allocation and otherwise leave a newly
			// reflected block without its mapped-frame view.
			EnsureUniformDrawSlotCapacity(drawUniformSlot + 1);
			if (!EnsureDescriptorUniformBuffers(program.DescriptorBindings))
			{
				reason = $"auto-uniform buffers unavailable ({_lastDescriptorPreparationFailure})";
				return false;
			}

			UpdateEngineUniformBuffersForDraw(frameIndex, drawUniformSlot, draw);
			if (!UpdateAutoUniformBuffersForDraw(
					frameIndex,
					drawUniformSlot,
					material,
					draw,
					out string autoUniformFailure))
			{
				reason = $"auto uniforms: {autoUniformFailure}";
				return false;
			}
			if (!TryPrepareFrameDataPayloadHandles(
					preparedFrame,
					program,
					frameIndex,
					drawUniformSlot,
					material,
					draw,
					out VulkanPreparedStreamRange payloadRange,
					out reason))
			{
				return false;
			}
			DescriptorSet[]? descriptorSets = null;
			DescriptorHeapPushDataPayload? descriptorHeapPushData = null;
			int preparedDescriptorHeapPushDwordCount = 0;
			VulkanPreparedStreamRange descriptorRange = default;
			VulkanPreparedStreamRange dynamicOffsetRange = default;
			VulkanPreparedStreamRange heapDwordRange = default;
			bool usesDescriptorHeap = BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap;
			bool requiresDescriptors =
				program.DescriptorSetLayouts.Count > 0 &&
				program.DescriptorBindings.Count > 0;
			if (requiresDescriptors)
			{
				if (usesDescriptorHeap &&
					!RefreshDescriptorHeapAutoUniformBindings(
						frameIndex,
						drawUniformSlot,
						out reason))
				{
					return false;
				}

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
					!TryPrepareDescriptorBindings(
						preparedFrame,
						program,
						descriptorSets,
						advancedSceneState,
						frameIndex,
						drawUniformSlot,
						out descriptorRange,
						out dynamicOffsetRange,
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
					heapDwordRange = preparedFrame.ReserveDescriptorHeapPushDwords(preparedDescriptorHeapPushDwordCount);
					for (int dwordIndex = 0; dwordIndex < preparedDescriptorHeapPushDwordCount; dwordIndex++)
						preparedFrame.SetDescriptorHeapPushDword(heapDwordRange, dwordIndex, descriptorHeapPushData.Dwords[dwordIndex]);
				}
			}

			// The cold record remains render-thread authority for validation and
			// diagnostics. Worker encoding consumes only the frozen scalar state
			// and prepared numeric streams below.
			DescriptorHeapPushDataIdentity descriptorHeapPushDataIdentity =
				program.DescriptorHeapLayout is { PushByteCount: > 0 } preparedHeapLayout
					? descriptorHeapPushData?.CaptureIdentity(preparedHeapLayout) ?? default
					: DescriptorHeapPushDataIdentity.CompleteNotUsed;
			int coldDataIndex = preparedFrame.AddMeshDrawColdData(new VulkanPreparedMeshDrawColdData(this, program, target, context, BackendContext.Resources.MappedFrameArena?.Generation ?? 0UL, Mesh?.Name ?? "<unnamed mesh>"));
			recordingState = new VulkanPreparedMeshDrawState(
				program.PipelineLayout,
				VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(
					BackendContext.DeviceContext),
				usesDescriptorHeap,
				program.DescriptorHeapLayout?.ShaderConstantByteCount ?? 0u,
				program.DescriptorHeapLayout?.PushByteCount ?? 0u,
				descriptorHeapPushDataIdentity,
				descriptorRange,
				dynamicOffsetRange,
				default,
				default,
				heapDwordRange,
				descriptorHeapPushData is not null && program.DescriptorHeapLayout is { } resourceLayout
					? descriptorHeapPushData.SnapshotResourceGenerations(resourceLayout) : [],
				vertexRange,
				primitive0,
				primitive1,
				primitive2,
				primitiveCount,
				frameIndex,
				drawUniformSlot,
				payloadRange,
				CreatePerDrawPushConstants(material, draw),
				draw.Instances,
				coldDataIndex);
			if (!residentHit && residentLookupAvailable)
			{
				_ = TryPublishResidentDrawTemplate(
					draw,
					passIndex,
					material,
					renderPass,
					useDynamicRendering,
					context,
					program.PipelineLayout,
					primitive0,
					primitive1,
					primitive2,
					primitiveCount,
					preparedFrame.GetVertexBuffers(vertexRange),
					preparedFrame.GetVertexBindings(vertexRange),
					residentVariant,
					residentGenerations);
			}
			reason = "Ready";
			return true;
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
		VulkanPreparedFrameRecording preparedFrame,
		VulkanTrackedCommandEncoder encoder)
	{
		ref readonly VulkanPreparedMeshDrawColdData cold = ref preparedFrame.GetMeshDrawColdData(recordingState.ColdDataIndex);
		if (recordingState.PipelineLayout.Handle == 0 ||
			recordingState.PrimitiveCount <= 0 ||
			!HasValidFrameDataPayloadHandles(recordingState, preparedFrame, in cold))
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
			if (!recordingState.VertexBuffers.IsEmpty)
			{
				ReadOnlySpan<VkBufferHandle> buffers = preparedFrame.GetVertexBuffers(recordingState.VertexBuffers);
				ReadOnlySpan<uint> bindings = preparedFrame.GetVertexBindings(recordingState.VertexBuffers);
				for (int bufferIndex = 0; bufferIndex < buffers.Length;
					 bufferIndex++)
				{
					encoder.BindVertexBuffer(
						commandBuffer,
						bindings[bufferIndex], buffers[bufferIndex]);
				}
			}

			encoder.PushConstants(
				commandBuffer,
				recordingState.PipelineLayout,
				recordingState.PushConstantStageFlags,
				recordingState.PushConstants);

			if (recordingState.UsesDescriptorHeap)
			{
				if (!encoder.TryPushDescriptorHeapProgramData(
						commandBuffer,
						recordingState.DescriptorHeapShaderConstantByteCount,
						recordingState.DescriptorHeapPushByteCount,
						preparedFrame.GetDescriptorHeapPushDwords(recordingState.DescriptorHeapPushDwords),
						recordingState.DescriptorHeapPushDwords.Count,
						recordingState.DescriptorHeapResourceGenerations))
				{
					return false;
				}
			}
			else if (!BindPreparedMeshDescriptorSets(
				encoder,
				commandBuffer,
				recordingState, preparedFrame, in cold))
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

	private static bool HasValidFrameDataPayloadHandles(in VulkanPreparedMeshDrawState state, VulkanPreparedFrameRecording frame, in VulkanPreparedMeshDrawColdData cold)
	{
		foreach (ref readonly VulkanPreparedFrameDataPayloadHandle handle in frame.GetFrameDataPayloadHandles(state.FrameDataPayloadHandles))
			if (!handle.IsValidFor(state.FrameIndex, state.DrawUniformSlot, cold.FrameDataGeneration)) return false;
		return true;
	}

	private bool TryPrepareFrameDataPayloadHandles(
		VulkanPreparedFrameRecording preparedFrame,
		VkRenderProgram program,
		int frameIndex,
		int drawUniformSlot,
		XRMaterial material,
		in PendingMeshDraw draw,
		out VulkanPreparedStreamRange range,
		out string reason)
	{
		range = default;
		reason = "Ready";

		int candidateCount = 0;
		for (int bindingIndex = 0;
			 bindingIndex < program.DescriptorBindings.Count;
			 bindingIndex++)
		{
			DescriptorBindingInfo binding = program.DescriptorBindings[bindingIndex];
			if (binding.DescriptorType is not (DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic))
				continue;
			if (program.TryGetAutoUniformBlock(binding.Set, binding.Binding, out _) ||
				GetEngineUniformSize(binding.Name ?? string.Empty) > 0)
				candidateCount++;
		}

		if (candidateCount == 0)
			return true;

		range = preparedFrame.ReserveFrameDataPayloadHandles(candidateCount);
		int handleIndex = 0;
		ulong arenaGeneration = BackendContext.Resources.MappedFrameArena?.Generation ?? 0UL;
		ulong materialGeneration = ComputeMaterialPublicationGeneration(
			material,
			material.BindingLayoutVersion,
			material.BindingValueVersion,
			draw.ProgramBindingSnapshot?.RuntimeUniformNameSignature ?? 0UL,
			draw.ProgramBindingSnapshot
				?.RuntimeUniformPublicationLayoutSignature ?? 0UL,
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
				preparedFrame.SetFrameDataPayloadHandle(range, handleIndex++,
					new VulkanPreparedFrameDataPayloadHandle(
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
						materialGeneration));
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
			preparedFrame.SetFrameDataPayloadHandle(range, handleIndex++,
				new VulkanPreparedFrameDataPayloadHandle(
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
					materialGeneration));
		}

		return true;
	}

	private bool TryPrepareDescriptorBindings(
		VulkanPreparedFrameRecording preparedFrame,
		VkRenderProgram program,
		DescriptorSet[] descriptorSets,
		in VulkanAdvancedScenePublicationState advancedSceneState,
		int frameIndex,
		int drawUniformSlot,
		out VulkanPreparedStreamRange descriptorRange,
		out VulkanPreparedStreamRange dynamicOffsetRange,
		out string reason)
	{
		descriptorRange = default;
		dynamicOffsetRange = default;
		int preparedBindingCount = 0;
		reason = "Ready";

		int totalDynamicOffsetCount = 0;
		int descriptorSetCount = program.DescriptorSetLayouts.Count;
		for (int setIndex = 0; setIndex < descriptorSetCount; setIndex++)
		{
			DescriptorSet descriptorSet = ResolvePreparedDescriptorSet(
				program,
				descriptorSets,
				setIndex,
				advancedSceneState);
			if (descriptorSet.Handle == 0)
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

		descriptorRange = preparedFrame.ReserveDescriptorBindings(preparedBindingCount);
		if (totalDynamicOffsetCount > 0)
			dynamicOffsetRange = preparedFrame.ReserveDynamicOffsets(totalDynamicOffsetCount);

		int preparedBindingIndex = 0;
		int dynamicOffsetIndex = 0;
		for (int setIndex = 0; setIndex < descriptorSetCount; setIndex++)
		{
			DescriptorSet descriptorSet = ResolvePreparedDescriptorSet(
				program,
				descriptorSets,
				setIndex,
				advancedSceneState);
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
					!TryResolveDynamicUniformOffset(
						program,
						binding,
						frameIndex,
						drawUniformSlot,
						default,
						out uint offset))
				{
					reason =
						$"dynamic uniform '{binding.Name}' in descriptor set {setIndex} could not resolve a bounded offset";
					return false;
				}

				preparedFrame.SetDynamicOffset(dynamicOffsetRange, dynamicOffsetIndex++, offset);
			}

			preparedFrame.SetDescriptorBinding(descriptorRange, preparedBindingIndex++,
				new VulkanPreparedDescriptorSetBinding(
					descriptorSet,
					(uint)setIndex,
					dynamicOffsetStart,
					dynamicOffsetIndex - dynamicOffsetStart));
		}

		return true;
	}

	private static DescriptorSet ResolvePreparedDescriptorSet(
		VkRenderProgram program,
		DescriptorSet[] descriptorSets,
		int setIndex,
		in VulkanAdvancedScenePublicationState advancedSceneState)
	{
		uint set = (uint)setIndex;
		if (program.IsDescriptorSetExternallyOwned(set))
		{
			return set switch
			{
				VulkanAdvancedSceneProgramBindingContract.GlobalSetIndex =>
					advancedSceneState.GlobalDescriptorSet,
				VulkanAdvancedSceneProgramBindingContract.ResourceSetIndex =>
					advancedSceneState.ResourceDescriptorSet,
				_ => default,
			};
		}

		return (uint)setIndex < (uint)descriptorSets.Length
			? descriptorSets[setIndex]
			: default;
	}

	private static bool BindPreparedMeshDescriptorSets(
		VulkanTrackedCommandEncoder encoder,
		CommandBuffer commandBuffer,
		in VulkanPreparedMeshDrawState recordingState,
		VulkanPreparedFrameRecording preparedFrame,
		in VulkanPreparedMeshDrawColdData cold)
	{
		if (recordingState.DescriptorBindings.IsEmpty)
			return true;
		ReadOnlySpan<VulkanPreparedDescriptorSetBinding> descriptorBindings = preparedFrame.GetDescriptorBindings(recordingState.DescriptorBindings);

		if (!encoder.TryAcquireFrameDataLease(
				commandBuffer,
				recordingState.DrawUniformSlot,
			cold.FrameDataGeneration,
				out _))
		{
			return false;
		}

		Span<DescriptorSet> oneSet = stackalloc DescriptorSet[1];
		for (int bindingIndex = 0;
			 bindingIndex < descriptorBindings.Length;
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
				ReadOnlySpan<uint> dynamicOffsets = preparedFrame.GetDynamicOffsets(recordingState.DynamicOffsets);
				if (binding.DynamicOffsetStart > dynamicOffsets.Length - binding.DynamicOffsetCount)
				{
					return false;
				}

				offsets = dynamicOffsets.Slice(
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
		int frameDataImageIndex,
		out IndexType indexType,
		out string failureReason)
	{
		indexType = IndexType.Uint32;
		failureReason = string.Empty;
		var material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
        bool preparedForRecord = draw.PreparedProgram is { } preparedProgram
            ? TryPrepareCapturedProgramForRecording(material, preparedProgram, draw.PreparedProgramIdentity, draw.PreparedProgramLinkGeneration, draw.ProgramBindingSnapshot, drawUniformSlot, frameDataImageIndex, out global::System.String prepareReason)
            : TryPrepareForRendering(material, out prepareReason);
        if (!preparedForRecord)
		{
			failureReason = $"mesh preparation failed: {prepareReason}; {LastPrepareDetail}";
			Debug.VulkanWarningEvery(
				$"Vulkan.IndirectDraw.PrepareSkip.{MeshRenderer.Name ?? "IndirectRenderer"}.{prepareReason}",
				TimeSpan.FromSeconds(2),
				"[Vulkan] Skipping indirect draw: {0}",
				failureReason);
			return false;
		}

		VkDataBuffer? indirectIndices = draw.IndirectTopology == EPrimitiveType.Points
			? _pointIndexBuffer : _triangleIndexBuffer;
		IndexSize indirectIndexSize = draw.IndirectTopology == EPrimitiveType.Points
			? _pointIndexSize : _triangleIndexSize;
		PrimitiveTopology indirectTopology = draw.IndirectTopology == EPrimitiveType.Points
			? PrimitiveTopology.PointList : PrimitiveTopology.TriangleList;
		if (!TryResolveIndexBinding(indirectIndices, indirectIndexSize, out VkBufferHandle indexHandle, out indexType, out uint indexCount) ||
			indexCount == 0)
		{
			failureReason = "indexed indirect input buffer is not ready or has no indices";
			Debug.VulkanWarningEvery(
				$"Vulkan.IndirectDraw.IndexMissing.{MeshRenderer.Name ?? "IndirectRenderer"}",
				TimeSpan.FromSeconds(2),
				"[Vulkan] Skipping indirect draw: {0}.", failureReason);
			return false;
		}

		if (!EnsurePipeline(material, indirectTopology, draw, renderPass, useDynamicRendering, dynamicRenderingFormats, passIndex, passMetadata, depthStencilReadOnly, pipelineName, allowPipelineCreation: false, out var pipeline))
		{
			failureReason = $"graphics pipeline is not ready for pass {passIndex} (pipeline '{pipelineName}', target '{targetName}')";
			return false;
		}

		CommandOperations.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

		if (!BindVertexBuffersForCurrentPipeline(commandBuffer, out failureReason))
			return false;

		if (_program?.Data is { } programData)
			NotifyDrawUniforms(material, programData, draw);

		// Heap descriptors share push-data storage with shader constants. Publish
		// the constants first so a shader with no constant block cannot have its
		// descriptor indices overwritten by the common mesh payload.
		PushPerDrawConstants(commandBuffer, material, draw);
		if (!BindDescriptorsIfAvailable(
				commandBuffer,
				material,
				draw,
				drawUniformSlot,
				frameDataImageIndex,
				passIndex,
				out failureReason))
			return false;

		CommandOperations.BindIndexBufferTracked(commandBuffer, indexHandle, 0, indexType);
		return true;
	}

	internal readonly record struct IndirectDrawRecordingState(
		VkRenderProgram Program,
		Pipeline Pipeline,
		PipelineLayout PipelineLayout,
		DescriptorSet[]? DescriptorSets,
		DescriptorHeapPushDataPayload? DescriptorHeapPushData,
		DescriptorHeapPushDataIdentity DescriptorHeapPushDataIdentity,
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
		ActivateDrawMaterializationSnapshot(draw);

        var material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
        bool preparedForRecord = draw.PreparedProgram is { } preparedProgram
			? TryPrepareCapturedProgramForRecording(material, preparedProgram, draw.PreparedProgramIdentity, draw.PreparedProgramLinkGeneration, draw.ProgramBindingSnapshot, drawUniformSlot, (int)Math.Min(imageIndex, int.MaxValue), out reason)
			: TryPrepareForRendering(material, out reason);
		if (!preparedForRecord)
			return false;

		VkDataBuffer? indirectIndices = draw.IndirectTopology == EPrimitiveType.Points
			? _pointIndexBuffer : _triangleIndexBuffer;
		IndexSize indirectIndexSize = draw.IndirectTopology == EPrimitiveType.Points
			? _pointIndexSize : _triangleIndexSize;
		PrimitiveTopology indirectTopology = draw.IndirectTopology == EPrimitiveType.Points
			? PrimitiveTopology.PointList : PrimitiveTopology.TriangleList;
		if (!TryResolveIndexBinding(indirectIndices, indirectIndexSize, out VkBufferHandle indexHandle, out IndexType indexType, out uint indexCount) ||
			indexCount == 0)
		{
			reason = "indexed indirect input buffer is not ready";
			return false;
		}

		if (!EnsurePipeline(material, indirectTopology, draw, renderPass, useDynamicRendering, dynamicRenderingFormats, passIndex, passMetadata, depthStencilReadOnly, pipelineName, allowPipelineCreation: true, out var pipeline))
		{
			reason = "pipeline pending";
			return false;
		}

		if (_program is not { } program)
		{
			reason = "program missing after pipeline preparation";
			return false;
		}

		if (!TryCreateIndirectVertexBufferSnapshot(out Silk.NET.Vulkan.Buffer[]? vertexBuffers, out global::System.UInt32[]? vertexBindings, out int vertexBufferCount, out reason))
			return false;

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
					return false;
				}

				if (_descriptorSets is null || _descriptorSets.Length == 0)
				{
					reason = "descriptor set array is empty";
					return false;
				}

				int descriptorSlotIndex = ResolveDescriptorFrameIndex(frameIndex, _descriptorSets.Length);
				UpdateEngineUniformBuffersForDraw(frameIndex, drawUniformSlot, draw);
				if (!UpdateAutoUniformBuffersForDraw(
						frameIndex,
						drawUniformSlot,
						material,
						draw,
						out string autoUniformFailure))
				{
					reason = $"auto uniforms: {autoUniformFailure}";
					return false;
				}
				if (!RefreshDescriptorHeapAutoUniformBindings(
						frameIndex,
						drawUniformSlot,
						out string autoUniformHeapReason))
				{
					reason = $"descriptor heap auto uniforms: {autoUniformHeapReason}";
					return false;
				}

				descriptorSets = _descriptorSets[descriptorSlotIndex];
				if (descriptorSets.Length == 0)
				{
					reason = $"descriptor set array at imageIndex {frameIndex}, drawSlot {drawUniformSlot} is empty";
					return false;
				}

				if (_activeDescriptorAllocation?.DescriptorHeapPushData is { Length: > 0 } heapPayloads &&
					(uint)descriptorSlotIndex < (uint)heapPayloads.Length)
				{
					descriptorHeapPushData = heapPayloads[descriptorSlotIndex];
				}
			}

			DescriptorHeapPushDataIdentity descriptorHeapPushDataIdentity =
				program.DescriptorHeapLayout is { PushByteCount: > 0 } heapLayout
					? descriptorHeapPushData?.CaptureIdentity(heapLayout) ?? default
					: DescriptorHeapPushDataIdentity.CompleteNotUsed;
			recordingState = new IndirectDrawRecordingState(
				program,
				pipeline,
				program.PipelineLayout,
				descriptorSets,
				descriptorHeapPushData,
				descriptorHeapPushDataIdentity,
				vertexBuffers,
				vertexBindings,
				vertexBufferCount,
				indexHandle,
				indexType,
				(int)Math.Min(imageIndex, int.MaxValue),
				drawUniformSlot,
				BackendContext.Resources.MappedFrameArena?.Generation ?? 0UL,
				CreatePerDrawPushConstants(material, draw));

			return true;
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
			VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(
				BackendContext.DeviceContext),
			0,
			recordingState.PushConstants);

		if (BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
		{
			if (recordingState.DescriptorHeapPushDataIdentity != DescriptorHeapPushDataIdentity.CompleteNotUsed &&
				(recordingState.Program.DescriptorHeapLayout is not { } heapLayout ||
				 recordingState.DescriptorHeapPushData?.CaptureIdentity(heapLayout) !=
				 recordingState.DescriptorHeapPushDataIdentity))
			{
				WarnOnce($"Skipping prepared draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because descriptor heap push data changed after preparation.");
				return false;
			}
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

	private bool TryPrepareVertexBufferSnapshot(
		VulkanPreparedFrameRecording preparedFrame,
		out VulkanPreparedStreamRange range,
		out string reason)
	{
		lock (_bufferStateSync)
		{
			int vertexBufferCount = _vertexBindings.Length;
			range = default;
			reason = "Ready";
			if (vertexBufferCount == 0)
				return true;

			range = preparedFrame.ReserveVertexBuffers(vertexBufferCount);
			for (int index = 0; index < vertexBufferCount; index++)
			{
				VertexInputBindingDescription binding = _vertexBindings[index];
				if (!_vertexBuffersByBinding.TryGetValue(binding.Binding, out VkDataBuffer? sourceBuffer))
				{
					reason = $"vertex binding {binding.Binding} has no backing buffer";
					return false;
				}

				if (!sourceBuffer.TryEnsureReadyForRendering(BackendContext.Resources.AllowSynchronousResourceUploads))
				{
					reason = $"vertex binding {binding.Binding} buffer is not ready";
					return false;
				}

				if (sourceBuffer.BufferHandle is not { } handle || handle.Handle == 0)
				{
					reason = $"vertex binding {binding.Binding} buffer is not allocated";
					return false;
				}

				preparedFrame.SetVertexBuffer(range, index, handle, binding.Binding);
			}

			return true;
		}
	}

	// Indirect recording owns this immutable snapshot until the secondary-command
	// batch finishes. It is deliberately not pooled because each state is consumed
	// once and batch-local storage clears the state afterwards.
	private bool TryCreateIndirectVertexBufferSnapshot(
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

			for (int i = 0; i < vertexBufferCount; i++)
			{
				VertexInputBindingDescription binding = _vertexBindings[i];
				if (!_vertexBuffersByBinding.TryGetValue(binding.Binding, out VkDataBuffer? sourceBuffer))
				{
					reason = $"vertex binding {binding.Binding} has no backing buffer";
					return false;
				}

				if (!sourceBuffer.TryEnsureReadyForRendering(BackendContext.Resources.AllowSynchronousResourceUploads))
				{
					reason = $"vertex binding {binding.Binding} buffer is not ready";
					return false;
				}

				if (sourceBuffer.BufferHandle is not { } handle || handle.Handle == 0)
				{
					reason = $"vertex binding {binding.Binding} buffer is not allocated";
					return false;
				}
			}

			vertexBuffers = new VkBufferHandle[vertexBufferCount];
			vertexBindings = new uint[vertexBufferCount];
			for (int i = 0; i < vertexBufferCount; i++)
			{
				VertexInputBindingDescription binding = _vertexBindings[i];
				vertexBindings[i] = binding.Binding;
				vertexBuffers[i] = _vertexBuffersByBinding[binding.Binding].BufferHandle!.Value;
			}

			return true;
		}
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
		MeshRenderMaterialResolver.ApplyShadowUniforms(
			programData,
			material,
			draw.ShadowUniformState,
			draw.ShadowCasterRelevance);
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
		=> BindVertexBuffersForCurrentPipeline(commandBuffer, out _);

	private bool BindVertexBuffersForCurrentPipeline(CommandBuffer commandBuffer, out string failureReason)
	{
		failureReason = string.Empty;
		lock (_bufferStateSync)
		{
			if (_vertexBindings.Length == 0)
				return true;

			foreach (VertexInputBindingDescription binding in _vertexBindings)
			{
				if (!_vertexBuffersByBinding.TryGetValue(binding.Binding, out VkDataBuffer? sourceBuffer))
				{
					failureReason = $"vertex binding {binding.Binding} has no backing buffer";
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because vertex binding {binding.Binding} has no backing buffer.");
					return false;
				}

				if (!sourceBuffer.TryEnsureReadyForRendering(BackendContext.Resources.AllowSynchronousResourceUploads))
				{
					failureReason = $"vertex binding {binding.Binding} buffer is not ready";
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because vertex binding {binding.Binding} buffer is not ready.");
					return false;
				}

				if (sourceBuffer.BufferHandle is not { } handle || handle.Handle == 0)
				{
					failureReason = $"vertex binding {binding.Binding} buffer is not allocated";
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
	internal bool BindDescriptorsIfAvailable(CommandBuffer commandBuffer, XRMaterial material, in PendingMeshDraw draw, int drawUniformSlot, int frameDataImageIndex, int passIndex)
		=> BindDescriptorsIfAvailable(commandBuffer, material, draw, drawUniformSlot, frameDataImageIndex, passIndex, out _);

	private bool BindDescriptorsIfAvailable(CommandBuffer commandBuffer, XRMaterial material, in PendingMeshDraw draw, int drawUniformSlot, int frameDataImageIndex, int passIndex, out string failureReason)
		=> BindDescriptorsIfAvailable(commandBuffer, material, draw,
			drawUniformSlot, frameDataImageIndex, passIndex,
			out failureReason, false, out _);

	private bool BindDescriptorsIfAvailable(CommandBuffer commandBuffer, XRMaterial material, in PendingMeshDraw draw, int drawUniformSlot, int frameDataImageIndex, int passIndex, out string failureReason, bool worldUiProofRequired, out VulkanWorldUiDescriptorBindingWitness worldUiWitness)
	{
		worldUiWitness = default;
		failureReason = string.Empty;
		if (_program is null)
			return !worldUiProofRequired;

		string meshName = Mesh?.Name ?? "UnnamedMesh";
		string programName = _program.Data?.Name ?? "UnnamedProgram";
		string materialName = material.Name ?? "UnnamedMaterial";
		int imageIndex = Math.Max(frameDataImageIndex, 0);
		bool traceWindowPresentation = VulkanMeshRenderingConventions.DescriptorTraceEnabled &&
			draw.WindowPresentationSourceMarker.HasSource;
		if (traceWindowPresentation)
		{
			Debug.VulkanEvery(
				$"Vulkan.FinalPresentation.BindDescriptors.Enter.{BindingId}.{programName}",
				TimeSpan.FromSeconds(1),
				"[VulkanDescriptor] final presentation BindDescriptorsIfAvailable entered mesh='{0}' prog='{1}' image={2} drawSlot={3} markerTexture={4} markerFbo={5}.",
				meshName, programName, imageIndex, drawUniformSlot,
				RuntimeHelpers.GetHashCode(draw.WindowPresentationSourceMarker.SourceTexture!),
				draw.WindowPresentationSourceMarker.SourceFrameBuffer is null ? 0 : RuntimeHelpers.GetHashCode(draw.WindowPresentationSourceMarker.SourceFrameBuffer));
		}

		bool requiresDescriptors = _program.DescriptorSetLayouts.Count > 0 && _program.DescriptorBindings.Count > 0;
		if (!requiresDescriptors)
		{
			if (traceWindowPresentation)
				Debug.VulkanEvery(
					$"Vulkan.FinalPresentation.BindDescriptors.NoDescriptors.{BindingId}.{programName}",
					TimeSpan.FromSeconds(1),
					"[VulkanDescriptor] final presentation BindDescriptorsIfAvailable result=true because program has no descriptor bindings mesh='{0}' prog='{1}'.",
					meshName, programName);
			return !worldUiProofRequired;
		}

		if (!EnsureDescriptorSets(material, drawUniformSlot, imageIndex, draw.ProgramBindingSnapshot))
		{
			failureReason = $"descriptor-set preparation failed: {_lastDescriptorPreparationFailure}";
			if (traceWindowPresentation)
				Debug.VulkanEvery(
					$"Vulkan.FinalPresentation.BindDescriptors.EnsureFailed.{BindingId}.{programName}",
					TimeSpan.FromSeconds(1),
					"[VulkanDescriptor] final presentation BindDescriptorsIfAvailable result=false because descriptor-set preparation failed mesh='{0}' prog='{1}' image={2} drawSlot={3}.",
					meshName, programName, imageIndex, drawUniformSlot);
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

		if (!TryRefreshFrameSourceDescriptorSetsForDraw(imageIndex, drawUniformSlot, material, draw.ProgramBindingSnapshot, commandBuffer, draw.WindowPresentationSourceMarker, out string frameSourceDescriptorReason))
		{
			failureReason = $"frame-source descriptor refresh failed: {frameSourceDescriptorReason}";
			if (traceWindowPresentation)
				Debug.VulkanEvery(
					$"Vulkan.FinalPresentation.BindDescriptors.FrameSourceFailed.{BindingId}.{programName}",
					TimeSpan.FromSeconds(1),
					"[VulkanDescriptor] final presentation BindDescriptorsIfAvailable result=false during frame-source refresh mesh='{0}' prog='{1}' reason='{2}'.",
					meshName, programName, frameSourceDescriptorReason);
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
			failureReason = "descriptor set array is null or empty";
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
		if (!UpdateAutoUniformBuffersForDraw(
				imageIndex,
				drawUniformSlot,
				material,
				draw,
				out string autoUniformFailure))
		{
			failureReason = $"auto-uniform update failed: {autoUniformFailure}";
			WarnOnce($"[DescFail] mesh={meshName} prog={programName} mat={materialName} reason=auto uniforms: {autoUniformFailure}");
			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
				programName,
				"auto-uniform",
				materialName,
				0,
				0,
				skippedDraw: true,
				skippedDispatch: false,
				$"mesh={meshName} auto uniforms: {autoUniformFailure}");
			return false;
		}

		DescriptorSet[] sets = _descriptorSets[descriptorSlotIndex];
		if (sets.Length == 0)
		{
			failureReason = $"descriptor set array at image index {imageIndex}, draw slot {drawUniformSlot} is empty";
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
			if (worldUiProofRequired)
			{
				failureReason = "retained world UI descriptor heap is unsupported";
				return false;
			}
			if (!RefreshDescriptorHeapAutoUniformBindings(
					imageIndex,
					drawUniformSlot,
					out string autoUniformHeapReason))
			{
				failureReason = $"descriptor heap auto-uniform refresh failed: {autoUniformHeapReason}";
				WarnOnce($"[DescFail] mesh={meshName} prog={programName} mat={materialName} reason=descriptor heap auto uniforms: {autoUniformHeapReason}");
				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
					programName,
					"descriptor-heap-auto-uniform",
					materialName,
					0,
					0,
					skippedDraw: true,
					skippedDispatch: false,
					$"mesh={meshName} descriptor heap auto uniforms: {autoUniformHeapReason}");
				return false;
			}

			DescriptorHeapPushDataPayload? payload = _activeDescriptorAllocation?.DescriptorHeapPushData is { Length: > 0 } heapPayloads &&
				(uint)descriptorSlotIndex < (uint)heapPayloads.Length
					? heapPayloads[descriptorSlotIndex]
					: null;
			if (!CommandOperations.TryPushDescriptorHeapProgramData(commandBuffer, _program, payload, out string heapReason))
			{
				failureReason = $"descriptor heap push failed: {heapReason}";
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

			if (traceWindowPresentation)
				Debug.VulkanEvery(
					$"Vulkan.FinalPresentation.BindDescriptors.Heap.{BindingId}.{programName}",
					TimeSpan.FromSeconds(1),
					"[VulkanDescriptor] final presentation BindDescriptorsIfAvailable result=true through descriptor heap mesh='{0}' prog='{1}' image={2}.",
					meshName, programName, imageIndex);
			return true;
		}

		if (worldUiProofRequired &&
			!TryBeginWorldUiDescriptorWitness(material, imageIndex,
				out worldUiWitness))
		{
			failureReason = "retained world UI descriptor owner is unavailable";
			return false;
		}
		bool bound = BindMeshDescriptorSets(
			commandBuffer,
			_program,
			_program.PipelineLayout,
			sets,
			imageIndex,
			drawUniformSlot,
			out failureReason, ref worldUiWitness);
		worldUiWitness.Complete = bound && worldUiProofRequired;
		if (traceWindowPresentation)
			Debug.VulkanEvery(
				$"Vulkan.FinalPresentation.BindDescriptors.Result.{BindingId}.{programName}.{bound}",
				TimeSpan.FromSeconds(1),
				"[VulkanDescriptor] final presentation BindDescriptorsIfAvailable result={0} mesh='{1}' prog='{2}' image={3} drawSlot={4}.",
				bound, meshName, programName, imageIndex, drawUniformSlot);
		return bound;
	}

	private bool BindMeshDescriptorSets(
		CommandBuffer commandBuffer,
		VkRenderProgram program,
		PipelineLayout pipelineLayout,
		DescriptorSet[] sets,
		int frameIndex,
		int drawUniformSlot,
		ulong sealedFrameDataGeneration = 0)
		=> BindMeshDescriptorSets(commandBuffer, program, pipelineLayout, sets, frameIndex, drawUniformSlot, out _, sealedFrameDataGeneration);

	private bool BindMeshDescriptorSets(
		CommandBuffer commandBuffer,
		VkRenderProgram program,
		PipelineLayout pipelineLayout,
		DescriptorSet[] sets,
		int frameIndex,
		int drawUniformSlot,
		out string failureReason,
		ulong sealedFrameDataGeneration = 0)
	{
		VulkanWorldUiDescriptorBindingWitness witness = default;
		return BindMeshDescriptorSets(commandBuffer, program,
			pipelineLayout, sets, frameIndex, drawUniformSlot,
			out failureReason, ref witness, sealedFrameDataGeneration);
	}

	private bool BindMeshDescriptorSets(
		CommandBuffer commandBuffer,
		VkRenderProgram program,
		PipelineLayout pipelineLayout,
		DescriptorSet[] sets,
		int frameIndex,
		int drawUniformSlot,
		out string failureReason,
		ref VulkanWorldUiDescriptorBindingWitness worldUiWitness,
		ulong sealedFrameDataGeneration = 0)
	{
		failureReason = string.Empty;
		if (!CommandOperations.TryAcquireMappedFrameArenaRecordingLease(
				commandBuffer,
				this,
				drawUniformSlot,
				sealedFrameDataGeneration,
				out string leaseReason))
		{
			failureReason = $"frame-data generation lease was rejected: {leaseReason}";
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
				failureReason = $"descriptor set {setIndex} has {dynamicOffsetCount} dynamic uniform offsets; limit is 64";
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
						commandBuffer,
						out uint offset))
				{
					failureReason = $"dynamic uniform '{binding.Name}' could not resolve a bounded offset";
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because dynamic uniform '{binding.Name}' could not resolve a bounded offset.");
					return false;
				}
				dynamicOffsets[offsetIndex++] = offset;
			}

			oneSet[0] = sets[setIndex];
			if (worldUiWitness.Allocation is not null &&
				!TryCaptureWorldUiBoundSet(ref worldUiWitness,
					setIndex, oneSet[0], dynamicOffsets))
			{
				failureReason = "retained world UI descriptor set lost native publication";
				return false;
			}
			CommandOperations.BindDescriptorSetsTracked(
				commandBuffer,
				PipelineBindPoint.Graphics,
				pipelineLayout,
				(uint)setIndex,
				oneSet,
				dynamicOffsets);
			if (worldUiWitness.Allocation is not null &&
				!WorldUiBoundSetCurrent(worldUiWitness, setIndex))
			{
				failureReason = "retained world UI native descriptor changed during binding";
				return false;
			}
		}
		return true;
	}

	private bool TryResolveDynamicUniformOffset(
		VkRenderProgram program,
		DescriptorBindingInfo binding,
		int frameIndex,
		int drawUniformSlot,
		CommandBuffer commandBuffer,
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
				ulong descriptorBaseOffset =
					BackendContext.Resources.Descriptors.Heap.ActiveBackend !=
						EVulkanDescriptorBackend.DescriptorHeap
						? 0UL
						: target.Offset;
				if (commandBuffer.Handle != 0)
				{
					CaptureDeferredLightingObjectBinding(
						program,
						block,
						frameIndex,
						drawUniformSlot,
						target.Buffer.Handle,
						descriptorBaseOffset,
						Math.Min(target.Size, block.Size),
						dynamicOffset,
						commandBuffer);
				}
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
	/// Prepares only cold, draw-invariant renderer state for a later primary-frame
	/// publication. This deliberately excludes render-data callbacks, scoped
	/// bindings, descriptor allocation/writes, and frame-source descriptors: those
	/// are current-frame state and must be published atomically by the recorder.
	/// </summary>
	internal bool TryPrepareFrameDataStructuresForRecording(
		in PendingMeshDraw draw,
		out string reason)
	{
		lock (_recordDrawSync)
			return TryPrepareFrameDataStructuresForRecordingNoLock(draw, out reason);
	}

	/// <summary>
	/// Transitions the native images published by the exact descriptor allocation
	/// that this draw will bind. A logical texture can rotate to a newer physical
	/// generation after submission, so re-resolving the texture is not sufficient.
	/// </summary>
	internal bool TryTransitionPreparedDescriptorImagesForSampling(
		CommandBuffer commandBuffer,
		in VulkanPreparedMeshDrawState recordingState,
		VulkanPreparedFrameRecording preparedFrame,
		XRFrameBuffer? target,
		int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata)
	{
		if (recordingState.UsesDescriptorHeap || recordingState.DescriptorBindings.IsEmpty)
		{
			return false;
		}

		bool transitionedPublishedSet = false;
		ReadOnlySpan<VulkanPreparedDescriptorSetBinding> descriptorBindings = preparedFrame.GetDescriptorBindings(recordingState.DescriptorBindings);
		for (int bindingIndex = 0;
			 bindingIndex < descriptorBindings.Length;
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
		ActivateDrawMaterializationSnapshot(draw);
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
				draw.WindowPresentationSourceMarker,
				out string frameSourceReason))
		{
			reason = $"frame-source descriptors pending: {frameSourceReason}";
			return false;
		}

		return true;
	}

	/// <summary>
	/// Re-selects the exact program and descriptor allocation for a draw after
	/// the frame-wide prewarm pass. A mesh renderer can participate in scene,
	/// directional, point, and spot passes in one frame, so its mutable active
	/// program/allocation cannot stand in for an earlier operation.
	/// </summary>
	private bool TryReactivatePrewarmedDrawForRecordingNoLock(
		in PendingMeshDraw draw,
		XRMaterial material,
		int drawUniformSlot,
		int frameIndex,
		out string reason)
	{
		reason = "Ready";
		if (draw.PreparedProgram is { } preparedProgram)
		{
			if (!ActivateCapturedProgram(
					material,
					preparedProgram,
					draw.PreparedProgramIdentity,
					draw.PreparedProgramLinkGeneration))
			{
				reason = "captured program pending after frame-wide prewarm";
				return false;
			}
		}
		else if (!EnsureProgram(material))
		{
			reason = "program pending after frame-wide prewarm";
			return false;
		}

		ApplyScopedProgramBindingsForPreparation(material);
		if (draw.ProgramBindingSnapshot is { } programBindingSnapshot)
			_program?.ApplyBindingSnapshot(programBindingSnapshot);

		if (!EnsureDescriptorSets(
				material,
				drawUniformSlot,
				frameIndex,
				draw.ProgramBindingSnapshot))
		{
			reason =
				$"descriptor allocation did not reactivate for captured program ({_lastDescriptorPreparationFailure})";
			return false;
		}

		if (!TryRefreshFrameSourceDescriptorSetsForDraw(
				frameIndex,
				drawUniformSlot,
				material,
				draw.ProgramBindingSnapshot,
				default,
				draw.WindowPresentationSourceMarker,
				out string frameSourceReason))
		{
			reason =
				$"frame-source descriptors did not reactivate for captured program: {frameSourceReason}";
			return false;
		}

		return true;
	}

	private void ActivateDrawMaterializationSnapshot(in PendingMeshDraw draw)
	{
		int descriptorViewFamilyIdentity =
			draw.DescriptorViewFamilyIdentity;
		if (_materializationSnapshot.DescriptorViewFamilyIdentity ==
			descriptorViewFamilyIdentity)
		{
			return;
		}

		_materializationSnapshot = _materializationSnapshot with
		{
			DescriptorViewFamilyIdentity = descriptorViewFamilyIdentity,
		};
	}

	private bool TryPrepareFrameDataStructuresForRecordingNoLock(
		in PendingMeshDraw draw,
		out string reason)
	{
		reason = "Ready";
		ActivateDrawMaterializationSnapshot(draw);
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

		BuildVertexInputState();
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
		bool foregroundRequired,
		out bool retryable,
		out string reason)
	{
		retryable = false;
		reason = string.Empty;
		if (!_recordDrawSync.TryEnter())
		{
			retryable = true;
			reason = "renderer recording state is busy";
			return false;
		}

		try
		{
			XRMaterial material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
			bool triangleIndexed = (_triangleIndexBuffer?.BufferHandle?.Handle ?? 0UL) != 0UL;
			bool lineIndexed = (_lineIndexBuffer?.BufferHandle?.Handle ?? 0UL) != 0UL;
			bool pointIndexed = (_pointIndexBuffer?.BufferHandle?.Handle ?? 0UL) != 0UL;
			bool triangleOnly = MeshRenderMaterialResolver.RequiresTriangleOnlyDrawsForCurrentPass();
			bool anyIndexed = triangleIndexed || (!triangleOnly && (lineIndexed || pointIndexed));
			bool ready = true;
			bool failuresRetryable = true;

			if (triangleIndexed)
			{
				bool topologyReady = EnsurePipelineCore(
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
					foregroundRequired,
					out _,
					out bool topologyRetryable,
					out string topologyReason);
				MergeGraphicsPipelinePrewarmResult(
					topologyReady,
					topologyRetryable,
					topologyReason,
					ref ready,
					ref failuresRetryable,
					ref reason);
			}

			if (!triangleOnly && lineIndexed)
			{
				bool topologyReady = EnsurePipelineCore(
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
					foregroundRequired,
					out _,
					out bool topologyRetryable,
					out string topologyReason);
				MergeGraphicsPipelinePrewarmResult(
					topologyReady,
					topologyRetryable,
					topologyReason,
					ref ready,
					ref failuresRetryable,
					ref reason);
			}

			if (!triangleOnly && pointIndexed)
			{
				bool topologyReady = EnsurePipelineCore(
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
					foregroundRequired,
					out _,
					out bool topologyRetryable,
					out string topologyReason);
				MergeGraphicsPipelinePrewarmResult(
					topologyReady,
					topologyRetryable,
					topologyReason,
					ref ready,
					ref failuresRetryable,
					ref reason);
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
					bool topologyReady = EnsurePipelineCore(
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
						foregroundRequired,
						out _,
						out bool topologyRetryable,
						out string topologyReason);
					MergeGraphicsPipelinePrewarmResult(
						topologyReady,
						topologyRetryable,
						topologyReason,
						ref ready,
						ref failuresRetryable,
						ref reason);
				}
			}

			retryable = !ready && failuresRetryable;
			if (ready)
				reason = "Ready";
			else if (reason.Length == 0)
				reason = "graphics pipeline preparation failed without a diagnostic";
			return ready;
		}
		finally
		{
			_recordDrawSync.Exit();
		}
	}

	private static void MergeGraphicsPipelinePrewarmResult(
		bool topologyReady,
		bool topologyRetryable,
		string topologyReason,
		ref bool ready,
		ref bool failuresRetryable,
		ref string firstFailureReason)
	{
		if (topologyReady)
			return;

		ready = false;
		failuresRetryable &= topologyRetryable;
		if (firstFailureReason.Length == 0)
			firstFailureReason = topologyReason;
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
			if (!UpdateAutoUniformBuffersForDraw(
					frameIndex,
					drawUniformSlot,
					material,
					draw,
					out string autoUniformFailure,
					frequencyMask))
			{
				reason = $"auto uniforms: {autoUniformFailure}";
				return false;
			}
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

	/// <summary>Identifies draws whose physical frame source must be published for every active descriptor slot.</summary>
	internal bool RequiresPerDrawFrameSourceDescriptorRefresh(in PendingMeshDraw draw)
	{
		lock (_recordDrawSync)
			return RequiresPerDrawFrameSourceDescriptorRefreshNoLock(draw);
	}

	private bool RequiresPerDrawFrameSourceDescriptorRefreshNoLock(in PendingMeshDraw draw)
	{
		if (draw.ProgramBindingSnapshot?.HasMutableFrameSourceSamplerBindings == true)
			return true;

		// A captured binding snapshot may precede the source's first publication.
		// Reflection still establishes that the draw needs slot-specific descriptor
		// observation, even when the snapshot has no ready source to classify yet.
		VkRenderProgram? program = draw.PreparedProgram ?? _program;
		if (program is null)
			return false;

		IReadOnlyList<DescriptorBindingInfo> bindings = program.DescriptorBindings;
		for (int index = 0; index < bindings.Count; index++)
		{
			DescriptorBindingInfo binding = bindings[index];
			if (IsImageDescriptorBinding(binding.DescriptorType) &&
				!program.IsDescriptorSetExternallyOwned(binding.Set) &&
				VulkanMeshRenderingConventions.IsFrameSourceSamplerName(binding.Name))
				return true;
		}
		return false;
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
			RequiresPerDrawFrameSourceDescriptorRefreshNoLock(draw))
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

		if (publicationProgram.BindingSchema is not { } bindingSchema)
			return "prepared binding schema unavailable";
		if (bindingSchema.HasUnknownAutoUniformFrequency)
			return "an auto-uniform block has unknown frequency";

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
		if (!HasReusableAutoUniformBuffers(out string autoUniformViewReason))
		{
			reason = $"descriptors {autoUniformViewReason}; command re-record required";
			return false;
		}
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
				draw.WindowPresentationSourceMarker,
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
			if (!UpdateAutoUniformBuffersForDraw(
					frameIndex,
					drawUniformSlot,
					material,
					draw,
					out string autoUniformFailure))
			{
				reason = $"auto uniforms: {autoUniformFailure}";
				return false;
			}
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
		lock (_recordDrawSync)
		{
			ActivateDrawMaterializationSnapshot(draw);
			XRMaterial material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
			bool prepared = draw.PreparedProgram is { } preparedProgram
				? TryPrepareCapturedProgramForRecording(
					material,
					preparedProgram,
					draw.PreparedProgramIdentity,
					draw.PreparedProgramLinkGeneration,
					draw.ProgramBindingSnapshot,
					drawUniformSlot,
					descriptorFrameIndex: 0,
					out string prepareReason)
				: TryPrepareForRendering(material, out prepareReason);
			if (!prepared)
				return $"render preparation failed: {prepareReason}; {LastPrepareDetail}";

			return CanReuseRecordedDescriptorSets(
				material,
				drawUniformSlot,
				resourcesCapturedByFrameSignature: draw.ProgramBindingSnapshot is not null,
				bindingSnapshot: draw.ProgramBindingSnapshot,
				out string reason)
				? "reusable descriptor sets; refresh likely failed after descriptor check"
				: reason;
		}
	}

    private void PushPerDrawConstants(CommandBuffer commandBuffer, XRMaterial material, in PendingMeshDraw draw)
	{
		if (_program is null)
			return;

		MeshDrawPushConstants constants = CreatePerDrawPushConstants(material, draw);
		CommandOperations.PushConstantsTracked(
			commandBuffer,
			_program.PipelineLayout,
			VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(
				BackendContext.DeviceContext),
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
