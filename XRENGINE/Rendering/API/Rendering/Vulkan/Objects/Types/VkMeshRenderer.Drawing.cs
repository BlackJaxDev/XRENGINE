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

			// Skinning and blendshape weights must be pushed to GPU before any draw
			// commands reference them (mirrors the OpenGL code path).
			MeshRenderer.PushBoneMatricesToGPU();
			MeshRenderer.PushBlendshapeWeightsToGPU();

			bool uniformsNotified = false;

			bool DrawIndexed(VkDataBuffer? indexBuffer, IndexSize size, PrimitiveTopology topology, Action<uint> onStats)
			{
				if (indexBuffer?.BufferHandle is not { } indexHandle)
					return false;

				if (size == IndexSize.Byte && !Renderer.SupportsIndexTypeUint8)
				{
					WarnOnce("Skipping indexed draw using byte-sized indices because Vulkan indexTypeUint8 is not enabled.");
					return false;
				}

				uint indexCount = indexBuffer.Data.ElementCount;
				if (indexCount == 0)
					return false;

				if (!EnsurePipeline(material, topology, drawCopy, renderPass, useDynamicRendering, colorAttachmentFormat, depthAttachmentFormat, out var pipeline))
					return false;

				Renderer.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

				if (!BindVertexBuffersForCurrentPipeline(commandBuffer))
					return false;

				if (!uniformsNotified && _program?.Data is { } programData)
				{
					MeshRenderer.OnSettingUniforms(programData, programData);
					uniformsNotified = true;
				}

				if (!BindDescriptorsIfAvailable(commandBuffer, material, drawCopy))
					return false;

				Renderer.BindIndexBufferTracked(commandBuffer, indexHandle, 0, ToVkIndexType(size));
				Api!.CmdDrawIndexed(commandBuffer, indexCount, drawInstances, 0, 0, 0);

				Engine.Rendering.Stats.IncrementDrawCalls();
				onStats(indexCount);
				return true;
			}

			// Attempt indexed draws for each primitive type in priority order.
			// The first successful draw sets 'drew = true' so we skip the non-indexed fallback.
			bool drew = false;
			drew |= DrawIndexed(_triangleIndexBuffer, _triangleIndexSize, PrimitiveTopology.TriangleList, count => Engine.Rendering.Stats.AddTrianglesRendered((int)(count / 3 * drawInstances)));
			drew |= DrawIndexed(_lineIndexBuffer, _lineIndexSize, PrimitiveTopology.LineList, _ => { });
			drew |= DrawIndexed(_pointIndexBuffer, _pointIndexSize, PrimitiveTopology.PointList, _ => { });

			if (!drew && Mesh is not null)
			{
				uint vertexCount = (uint)Math.Max(Mesh.VertexCount, 0);
				if (vertexCount > 0 && EnsurePipeline(material, PrimitiveTopology.TriangleList, drawCopy, renderPass, useDynamicRendering, colorAttachmentFormat, depthAttachmentFormat, out var pipeline))
				{
					Renderer.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

					if (!BindVertexBuffersForCurrentPipeline(commandBuffer))
						return;

					if (!uniformsNotified && _program?.Data is { } programData)
					{
						MeshRenderer.OnSettingUniforms(programData, programData);
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
				return false;

			if (_descriptorSets is null || _descriptorSets.Length == 0)
				return false;

			if (imageIndex >= _descriptorSets.Length)
				imageIndex = 0;

			UpdateEngineUniformBuffersForDraw(imageIndex, draw);
			UpdateAutoUniformBuffersForDraw(imageIndex, material, draw);

			DescriptorSet[] sets = _descriptorSets[imageIndex];
			if (sets.Length == 0)
				return false;

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
