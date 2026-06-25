// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Drawing.cs  – partial class: Draw Command Recording
//
// Records indexed and non-indexed draw commands into Vulkan command buffers.
// Handles vertex buffer binding, descriptor set binding, and per-draw uniform
// notification.
// ──────────────────────────────────────────────────────────────────────────────

using System;
using System.Numerics;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
	public partial class VkMeshRenderer
	{
		#region Draw Command Recording

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
			int drawUniformSlot)
		{
			var material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
			string prepareReason;
			bool preparedForRecord = draw.PreparedProgram is { } preparedProgram
				? TryPrepareCapturedProgramForRecording(material, preparedProgram, draw.PreparedProgramIdentity, drawUniformSlot, out prepareReason)
				: TryPrepareForRendering(material, out prepareReason);
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

			// Skinning and blendshape weights must be pushed to GPU before any draw
			// commands reference them (mirrors the OpenGL code path).
			MeshRenderer.PushBoneMatricesToGPU();
			MeshRenderer.PushBlendshapeWeightsToGPU();

			bool uniformsNotified = false;

			bool DrawIndexed(VkDataBuffer? indexBuffer, IndexSize size, PrimitiveTopology topology)
			{
				if (indexBuffer?.BufferHandle is not { } indexHandle)
				{
					if (verboseTrace)
						Debug.MeshesWarning("[DrawTrace] {0}: no indexBuffer for {1}", Mesh?.Name ?? "?", topology);
					return false;
				}

				if (size == IndexSize.Byte && !Renderer.SupportsIndexTypeUint8)
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

				if (!EnsurePipeline(material, topology, drawCopy, renderPass, useDynamicRendering, dynamicRenderingFormats, passIndex, passMetadata, depthStencilReadOnly, pipelineName, out var pipeline))
				{
					if (verboseTrace)
						Debug.MeshesWarning("[DrawTrace] {0}: EnsurePipeline FAILED for {1} dynRender={2} colors={3} depthFmt={4}",
							Mesh?.Name ?? "?", topology, useDynamicRendering, dynamicRenderingFormats.DescribeColorFormats(), dynamicRenderingFormats.DepthAttachmentFormat);
					return false;
				}

				Renderer.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

				if (!BindVertexBuffersForCurrentPipeline(commandBuffer))
				{
					if (verboseTrace)
						Debug.MeshesWarning("[DrawTrace] {0}: BindVertexBuffers FAILED", Mesh?.Name ?? "?");
					return false;
				}

				if (!uniformsNotified && _program?.Data is { } programData)
				{
					NotifyDrawUniforms(material, programData, drawCopy);
					uniformsNotified = true;
				}

				if (!BindDescriptorsIfAvailable(commandBuffer, material, drawCopy, drawUniformSlot))
				{
					if (verboseTrace)
						Debug.MeshesWarning("[DrawTrace] {0}: BindDescriptors FAILED", Mesh?.Name ?? "?");
					return false;
				}

				PushPerDrawConstants(commandBuffer, material, drawCopy);

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

				Renderer.BindIndexBufferTracked(commandBuffer, indexHandle, 0, ToVkIndexType(size));
				Api!.CmdDrawIndexed(commandBuffer, indexCount, drawInstances, 0, 0, 0);

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

				if (vertexCount > 0 && EnsurePipeline(material, fallbackTopology, drawCopy, renderPass, useDynamicRendering, dynamicRenderingFormats, passIndex, passMetadata, depthStencilReadOnly, pipelineName, out var pipeline))
				{
					Renderer.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

					if (!BindVertexBuffersForCurrentPipeline(commandBuffer))
						return false;

					if (!uniformsNotified && _program?.Data is { } programData)
					{
						NotifyDrawUniforms(material, programData, drawCopy);
						uniformsNotified = true;
					}

					if (!BindDescriptorsIfAvailable(commandBuffer, material, drawCopy, drawUniformSlot))
						return false;

					PushPerDrawConstants(commandBuffer, material, drawCopy);

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

					if (BloomVulkanDiagnosticsEnabled && vertexCount <= 6u)
					{
						Debug.VulkanEvery(
							$"Vulkan.BloomDiag.CmdDraw.{passIndex}.{Renderer.GetCurrentDrawFrameBuffer()?.Name}.{vertexCount}",
							TimeSpan.FromSeconds(1),
							"[BloomDiag][Vulkan] CmdDraw fullscreen target='{0}' pass={1} vertices={2} pipeline=0x{3:X} topology={4} blend={5} depthTest={6} depthWrite={7} stencil={8} colorWrite={9} viewport=({10},{11},{12},{13}) scissor=({14},{15},{16},{17}) program='{18}' material='{19}'",
							Renderer.GetCurrentDrawFrameBuffer()?.Name ?? "<none>",
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

					Api!.CmdDraw(commandBuffer, vertexCount, drawInstances, 0, 0);
					drew = true;
				}
			}

			return drew;
		}

		private void NotifyDrawUniforms(XRMaterial material, XRRenderProgram programData, in PendingMeshDraw draw)
		{
			if (draw.ProgramBindingSnapshot is { } snapshot && _program is not null)
			{
				LogGizmoBindingSnapshot(material, snapshot, "apply");
				_program.ApplyBindingSnapshot(snapshot);
				return;
			}

			Renderer.SetMaterialUniforms(material, programData, draw.ShadowUniformState);
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
			if (_vertexBindings.Length == 0)
				return true;

			foreach (VertexInputBindingDescription binding in _vertexBindings)
			{
				if (!_vertexBuffersByBinding.TryGetValue(binding.Binding, out VkDataBuffer? sourceBuffer))
				{
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because vertex binding {binding.Binding} has no backing buffer.");
					return false;
				}

				if (!sourceBuffer.TryEnsureReadyForRendering(Renderer.AllowSynchronousResourceUploads))
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
				Renderer.BindVertexBuffersTracked(commandBuffer, binding.Binding, _singleVertexBindingBuffer, _singleVertexBindingOffset);
			}

			return true;
		}

		/// <summary>
		/// Binds descriptor sets for the current mesh draw. Mesh draws must use the
		/// renderer-owned descriptor path because it carries per-draw engine and auto
		/// uniform buffers in addition to material resources.
		/// </summary>
		private bool BindDescriptorsIfAvailable(CommandBuffer commandBuffer, XRMaterial material, in PendingMeshDraw draw, int drawUniformSlot)
		{
			if (_program is null)
				return true;

			string meshName = Mesh?.Name ?? "UnnamedMesh";
			string programName = _program.Data?.Name ?? "UnnamedProgram";
			string materialName = material.Name ?? "UnnamedMaterial";

			bool requiresDescriptors = _program.DescriptorSetLayouts.Count > 0 && _program.DescriptorBindings.Count > 0;
			if (!requiresDescriptors)
				return true;

			int imageIndex = ResolveCommandBufferIndex(commandBuffer);
			if (imageIndex < 0)
				imageIndex = 0;

			if (!EnsureDescriptorSets(material, drawUniformSlot))
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

			int descriptorSlotIndex = ResolveUniformBufferIndex(imageIndex, drawUniformSlot, _descriptorSets.Length);

			UpdateEngineUniformBuffersForDraw(imageIndex, drawUniformSlot, draw);
			UpdateAutoUniformBuffersForDraw(imageIndex, drawUniformSlot, material, draw);

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

			Renderer.BindDescriptorSetsTracked(commandBuffer, PipelineBindPoint.Graphics, _program.PipelineLayout, 0, sets);
			return true;
		}

		internal bool TryRefreshReusableCommandBufferFrameData(uint imageIndex, in PendingMeshDraw draw, int drawUniformSlot, bool refreshMaterialUniforms = true)
			=> TryRefreshReusableCommandBufferFrameData(imageIndex, draw, drawUniformSlot, out _, refreshMaterialUniforms);

		internal bool TryPrewarmFrameDataForRecording(in PendingMeshDraw draw, int drawUniformSlot, out string reason)
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
				ActivateCapturedProgram(material, preparedProgram, draw.PreparedProgramIdentity);
			else if (!EnsureProgram(material))
			{
				reason = "program pending";
				return false;
			}

			EnsureRuntimeDeformationBuffersCurrent();
			EnsureBuffers(ProgramUsesShaderGeneratedVertices());

			if (!AreCachedBuffersReadyForRendering(out string bufferDetail))
			{
				reason = $"buffers not ready: {bufferDetail}";
				return false;
			}

			ApplyScopedProgramBindingsForPreparation(material);
			if (_program?.Data is { } programData)
				NotifyDrawUniforms(material, programData, draw);

			BuildVertexInputState();

			if (!EnsureDescriptorSets(material, drawUniformSlot))
			{
				reason = $"descriptors pending; program='{_program?.Data?.Name ?? "<unnamed program>"}'";
				return false;
			}

			return true;
		}

		internal bool TryRefreshReusableCommandBufferFrameData(
			uint imageIndex,
			in PendingMeshDraw draw,
			int drawUniformSlot,
			out string reason,
			bool refreshMaterialUniforms = true)
		{
			reason = "reusable";
			XRMaterial material = draw.MaterialOverride ?? ResolveMaterial(null, draw.Instances);
			if (draw.PreparedProgram is { } preparedProgram)
			{
				ActivateCapturedProgram(material, preparedProgram, draw.PreparedProgramIdentity);
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

			if (!AreCachedBuffersReadyForRendering(out string bufferDetail))
			{
				reason = $"buffers not ready: {bufferDetail}";
				return false;
			}

			if (refreshMaterialUniforms && _program?.Data is { } programData)
				NotifyDrawUniforms(material, programData, draw);

			if (!CanReuseRecordedDescriptorSets(material, drawUniformSlot, draw.ProgramBindingSnapshot is not null, out string descriptorReason))
			{
				reason = $"descriptors {descriptorReason}; snapshot={(draw.ProgramBindingSnapshot is null ? "none" : "captured")} program='{_program?.Data?.Name ?? "<unnamed program>"}'";
				return false;
			}

			int frameIndex = unchecked((int)Math.Min(imageIndex, int.MaxValue));
			UpdateEngineUniformBuffersForDraw(frameIndex, drawUniformSlot, draw);
			if (refreshMaterialUniforms)
				UpdateAutoUniformBuffersForDraw(frameIndex, drawUniformSlot, material, draw);
			return true;
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

		private readonly struct MeshDrawPushConstants
		{
			public readonly uint MaterialIdentity;
			public readonly uint InstanceCount;
			public readonly uint BillboardMode;
			public readonly uint DebugFlags;

			public MeshDrawPushConstants(uint materialIdentity, uint instanceCount, uint billboardMode, uint debugFlags)
			{
				MaterialIdentity = materialIdentity;
				InstanceCount = instanceCount;
				BillboardMode = billboardMode;
				DebugFlags = debugFlags;
			}
		}

		private void PushPerDrawConstants(CommandBuffer commandBuffer, XRMaterial material, in PendingMeshDraw draw)
		{
			if (_program is null)
				return;

			uint debugFlags = 0;
			if (draw.IsStereoPass)
				debugFlags |= 1u;
			if (draw.UseUnjitteredProjection)
				debugFlags |= 2u;

			MeshDrawPushConstants constants = new(
				unchecked((uint)(material.GetHashCode() & int.MaxValue)),
				draw.Instances,
				(uint)draw.BillboardMode,
				debugFlags);

			Renderer.PushConstantsTracked(
				commandBuffer,
				_program.PipelineLayout,
				CommonPushConstantStageFlags,
				0,
				constants);
		}

		/// <summary>
		/// Computes a hash of the vertex input layout (bindings + attributes) for
		/// use as part of the pipeline cache key.
		/// </summary>
		private ulong ComputeVertexLayoutHash()
		{
			HashCode hash = new();
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

			return unchecked((ulong)hash.ToHashCode());
		}

		/// <summary>
		/// Maps a command buffer handle back to its swapchain image index.
		/// Returns -1 if the buffer is not in the current set (e.g. transient buffer).
		/// </summary>
		private int ResolveCommandBufferIndex(CommandBuffer commandBuffer)
			=> Renderer.ResolveCommandBufferImageIndex(commandBuffer);

		#endregion // Draw Command Recording
	}
}
