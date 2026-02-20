// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Drawing.cs  – partial class: Draw Command Recording
//
// Records indexed and non-indexed draw commands into Vulkan command buffers.
// Handles vertex buffer binding, descriptor set binding, and per-draw uniform
// notification.
// ──────────────────────────────────────────────────────────────────────────────

using System;
using System.Linq;
using System.Numerics;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

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
		internal void RecordDraw(
			CommandBuffer commandBuffer,
			in PendingMeshDraw draw,
			RenderPass renderPass,
			bool useDynamicRendering,
			Format colorAttachmentFormat,
			Format depthAttachmentFormat)
		{
			EnsureBuffers();

			var material = ResolveMaterial(draw.MaterialOverride);
			var drawCopy = draw; // struct copy required for capture in local function closures
			uint drawInstances = draw.Instances;

			// Trace swapchain (dynamic rendering) draws only.
			bool traceSwapchain = useDynamicRendering && renderPass.Handle == 0;
			bool verboseSwapchainTrace = traceSwapchain && string.Equals(
				Environment.GetEnvironmentVariable("XRE_VK_TRACE_SWAPDRAW"),
				"1",
				StringComparison.Ordinal);

			// Skinning and blendshape weights must be pushed to GPU before any draw
			// commands reference them (mirrors the OpenGL code path).
			MeshRenderer.PushBoneMatricesToGPU();
			MeshRenderer.PushBlendshapeWeightsToGPU();

			bool uniformsNotified = false;

			bool DrawIndexed(VkDataBuffer? indexBuffer, IndexSize size, PrimitiveTopology topology, Action<uint> onStats)
			{
				if (indexBuffer?.BufferHandle is not { } indexHandle)
				{
					if (verboseSwapchainTrace)
						Debug.RenderingWarning("[SwapDraw] {0}: no indexBuffer for {1}", Mesh?.Name ?? "?", topology);
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
					if (verboseSwapchainTrace)
						Debug.RenderingWarning("[SwapDraw] {0}: indexCount=0 for {1}", Mesh?.Name ?? "?", topology);
					return false;
				}

				if (!EnsurePipeline(material, topology, drawCopy, renderPass, useDynamicRendering, colorAttachmentFormat, depthAttachmentFormat, out var pipeline))
				{
					if (verboseSwapchainTrace)
						Debug.RenderingWarning("[SwapDraw] {0}: EnsurePipeline FAILED for {1} dynRender={2} colorFmt={3} depthFmt={4}",
							Mesh?.Name ?? "?", topology, useDynamicRendering, colorAttachmentFormat, depthAttachmentFormat);
					return false;
				}

				Renderer.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

				if (!BindVertexBuffersForCurrentPipeline(commandBuffer))
				{
					if (verboseSwapchainTrace)
						Debug.RenderingWarning("[SwapDraw] {0}: BindVertexBuffers FAILED", Mesh?.Name ?? "?");
					return false;
				}

				if (!uniformsNotified && _program?.Data is { } programData)
				{
					MeshRenderer.OnSettingUniforms(programData, programData);
					material.OnSettingUniforms(programData);
					uniformsNotified = true;
				}

				if (!BindDescriptorsIfAvailable(commandBuffer, material, drawCopy))
				{
					if (verboseSwapchainTrace)
						Debug.RenderingWarning("[SwapDraw] {0}: BindDescriptors FAILED", Mesh?.Name ?? "?");
					return false;
				}

				if (verboseSwapchainTrace)
					Debug.RenderingWarning("[SwapDraw] {0}: CmdDrawIndexed({1}) pipeline=0x{2:X} topology={3} cull={4} blend={5} depthTest={6} depthWrite={7} depthCmp={8} colorWrite={9} viewport=({10},{11},{12},{13}) scissor=({14},{15},{16},{17}) prog={18}",
						Mesh?.Name ?? "?", indexCount, pipeline.Handle, topology,
						drawCopy.CullMode, drawCopy.BlendEnabled, drawCopy.DepthTestEnabled, drawCopy.DepthWriteEnabled, drawCopy.DepthCompareOp, drawCopy.ColorWriteMask,
						drawCopy.Viewport.X, drawCopy.Viewport.Y, drawCopy.Viewport.Width, drawCopy.Viewport.Height,
						drawCopy.Scissor.Offset.X, drawCopy.Scissor.Offset.Y, drawCopy.Scissor.Extent.Width, drawCopy.Scissor.Extent.Height,
						_program?.Data?.Name ?? "?prog");

				Renderer.BindIndexBufferTracked(commandBuffer, indexHandle, 0, ToVkIndexType(size));
				Api!.CmdDrawIndexed(commandBuffer, indexCount, drawInstances, 0, 0, 0);

				onStats(indexCount);
				return true;
			}

			// Attempt indexed draws for each primitive type in priority order.
			// The first successful draw sets 'drew = true' so we skip the non-indexed fallback.
			bool drew = false;
			if (_triangleIndexBuffer?.BufferHandle is { } triHandle && triHandle.Handle != 0)
				drew |= DrawIndexed(_triangleIndexBuffer, _triangleIndexSize, PrimitiveTopology.TriangleList, count => Engine.Rendering.Stats.AddTrianglesRendered((int)(count / 3 * drawInstances)));
			if (_lineIndexBuffer?.BufferHandle is { } lineHandle && lineHandle.Handle != 0)
				drew |= DrawIndexed(_lineIndexBuffer, _lineIndexSize, PrimitiveTopology.LineList, _ => { });
			if (_pointIndexBuffer?.BufferHandle is { } pointHandle && pointHandle.Handle != 0)
				drew |= DrawIndexed(_pointIndexBuffer, _pointIndexSize, PrimitiveTopology.PointList, _ => { });

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
				if (vertexCount > 0 && EnsurePipeline(material, fallbackTopology, drawCopy, renderPass, useDynamicRendering, colorAttachmentFormat, depthAttachmentFormat, out var pipeline))
				{
					Renderer.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

					if (!BindVertexBuffersForCurrentPipeline(commandBuffer))
						return;

					if (!uniformsNotified && _program?.Data is { } programData)
					{
						MeshRenderer.OnSettingUniforms(programData, programData);
						material.OnSettingUniforms(programData);
						uniformsNotified = true;
					}

					if (!BindDescriptorsIfAvailable(commandBuffer, material, drawCopy))
						return;

					Api!.CmdDraw(commandBuffer, vertexCount, drawInstances, 0, 0);
					Engine.Rendering.Stats.IncrementDrawCalls();
					Engine.Rendering.Stats.AddTrianglesRendered((int)(vertexCount / 3 * drawInstances));
				}
			}
		}

		/// <summary>
		/// Binds all vertex buffers required by the current pipeline's input bindings.
		/// Returns false (and warns) if any binding has no backing buffer.
		/// </summary>
		private bool BindVertexBuffersForCurrentPipeline(CommandBuffer commandBuffer)
		{
			if (_vertexBindings.Length == 0)
				return true;

			foreach (VertexInputBindingDescription binding in _vertexBindings.OrderBy(b => b.Binding))
			{
				if (!_vertexBuffersByBinding.TryGetValue(binding.Binding, out VkDataBuffer? sourceBuffer))
				{
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because vertex binding {binding.Binding} has no backing buffer.");
					return false;
				}

				sourceBuffer.Generate();
				if (sourceBuffer.BufferHandle is not { } handle || handle.Handle == 0)
				{
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because vertex binding {binding.Binding} buffer is not allocated.");
					return false;
				}

				Renderer.BindVertexBuffersTracked(commandBuffer, binding.Binding, [handle], [0UL]);
			}

			return true;
		}

		/// <summary>
		/// Binds descriptor sets for the current draw. First attempts VkMaterial-level
		/// binding; if unavailable, falls back to the per-renderer descriptor sets
		/// managed by this class.
		/// </summary>
		private bool BindDescriptorsIfAvailable(CommandBuffer commandBuffer, XRMaterial material, in PendingMeshDraw draw)
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

			if (Renderer.GetOrCreateAPIRenderObject(material, generateNow: true) is VkMaterial vkMaterial &&
				vkMaterial.TryBindDescriptorSets(commandBuffer, _program, imageIndex))
				return true;

			if (!EnsureDescriptorSets(material))
			{
				WarnOnce($"[DescFail] mesh={meshName} prog={programName} mat={materialName} reason=EnsureDescriptorSets returned false");
				return false;
			}

			if (_descriptorSets is null || _descriptorSets.Length == 0)
			{
				WarnOnce($"[DescFail] mesh={meshName} prog={programName} mat={materialName} reason=descriptor set array is null or empty");
				return false;
			}

			if (imageIndex >= _descriptorSets.Length)
				imageIndex = 0;

			UpdateEngineUniformBuffersForDraw(imageIndex, draw);
			UpdateAutoUniformBuffersForDraw(imageIndex, material, draw);

			DescriptorSet[] sets = _descriptorSets[imageIndex];
			if (sets.Length == 0)
			{
				WarnOnce($"[DescFail] mesh={meshName} prog={programName} mat={materialName} reason=descriptor set array at imageIndex {imageIndex} is empty");
				return false;
			}

			Renderer.BindDescriptorSetsTracked(commandBuffer, PipelineBindPoint.Graphics, _program.PipelineLayout, 0, sets);
			return true;
		}

		/// <summary>
		/// Computes a hash of the vertex input layout (bindings + attributes) for
		/// use as part of the pipeline cache key.
		/// </summary>
		private ulong ComputeVertexLayoutHash()
		{
			HashCode hash = new();
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
		{
			if (Renderer._commandBuffers is null)
				return -1;

			for (int i = 0; i < Renderer._commandBuffers.Length; i++)
			{
				if (Renderer._commandBuffers[i].Handle == commandBuffer.Handle)
					return i;
			}

			return -1;
		}

		#endregion // Draw Command Recording
	}
}
