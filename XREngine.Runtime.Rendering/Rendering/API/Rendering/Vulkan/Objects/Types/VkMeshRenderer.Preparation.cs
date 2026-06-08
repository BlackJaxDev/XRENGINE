using System;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
	public partial class VkMeshRenderer
	{
		public bool IsPreparedForRendering
			=> IsActive &&
			   _program is not null &&
			   !_buffersDirty &&
			   !_descriptorDirty &&
			   AreCachedBuffersReadyForRendering(out _) &&
			   string.Equals(_lastPrepareResult, "Ready", StringComparison.Ordinal);

		public string LastPrepareDetail => _lastPrepareDetail;

		public bool TryPrepareForRendering()
			=> TryPrepareForRendering(ResolveMaterial(null, 1u), out _);

		public bool TryPrepareForRendering(out string reason)
			=> TryPrepareForRendering(ResolveMaterial(null, 1u), out reason);

		private bool TryPrepareForRendering(XRMaterial material, out string reason)
		{
			reason = "Ready";

			if (!IsActive)
				Generate();

			if (!IsActive)
				return SetPrepareResult(false, "GenerateFailed", "Vulkan mesh renderer wrapper is not active.", out reason);

			if (Data is null)
				return SetPrepareResult(false, "DataMissing", "XRMeshRenderer.BaseVersion data is null.", out reason);

			if (ReferenceEquals(material, null))
				return SetPrepareResult(false, "MaterialMissing", "No material could be resolved for this draw.", out reason);

			if (!ReferenceEquals(_lastPreparedMaterial, material))
			{
				_pipelineDirty = true;
				_descriptorDirty = true;
				_lastPreparedMaterial = material;
			}

			if (MeshRenderer.HasRenderDataPreparation)
				MeshRenderer.OnPreparingRenderData();

			EnsureRuntimeDeformationBuffersCurrent();
			EnsureBuffers();

			if (!AreCachedBuffersReadyForRendering(out string bufferDetail))
				return SetPrepareResult(false, "BuffersPending", bufferDetail, out reason);

			if (!EnsureProgram(material))
				return SetPrepareResult(false, "ProgramsPending", "No compatible Vulkan render program is available yet.", out reason);

			BuildVertexInputState();

			if (!EnsureDescriptorSets(material))
				return SetPrepareResult(false, "DescriptorsPending", "Descriptor sets are not allocated or populated for the active program/material layout.", out reason);

			string layoutSummary = _geometryLayoutSignature.DebugSummary;
			return SetPrepareResult(true, "Ready", $"buffers=Ready; program={_program?.Data?.Name ?? "<unnamed>"}; descriptors=Ready; pipeline=DeferredUntilPass; layout={layoutSummary}", out reason);
		}

		private bool AreCachedBuffersReadyForRendering(out string detail)
		{
			foreach (var pair in _bufferCache)
			{
				VkDataBuffer buffer = pair.Value;
				if (!buffer.IsReadyForRendering)
				{
					detail = $"buffer='{pair.Key}' target={buffer.Data.Target} generated={buffer.IsGenerated} length={buffer.Data.Length} allocated={buffer.AllocatedByteSize}";
					return false;
				}
			}

			if (!IsIndexBufferReady(_triangleIndexBuffer, "Triangles", out detail))
				return false;
			if (!IsIndexBufferReady(_lineIndexBuffer, "Lines", out detail))
				return false;
			if (!IsIndexBufferReady(_pointIndexBuffer, "Points", out detail))
				return false;

			detail = string.Empty;
			return true;
		}

		private static bool IsIndexBufferReady(VkDataBuffer? buffer, string primitiveName, out string detail)
		{
			if (buffer is null)
			{
				detail = string.Empty;
				return true;
			}

			if (buffer.IsReadyForRendering)
			{
				detail = string.Empty;
				return true;
			}

			detail = $"indexBuffer='{primitiveName}' generated={buffer.IsGenerated} length={buffer.Data.Length} allocated={buffer.AllocatedByteSize}";
			return false;
		}

		private bool SetPrepareResult(bool ready, string result, string detail, out string reason)
		{
			_lastPrepareResult = result;
			_lastPrepareDetail = detail;
			reason = result;

			if (!ready)
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.MeshRenderer.NotReady.{MeshRenderer.Name ?? "UnnamedRenderer"}.{result}",
					TimeSpan.FromSeconds(2),
					"[Vulkan] Mesh renderer not ready: renderer='{0}' mesh='{1}' result={2}. {3}",
					MeshRenderer.Name ?? "<unnamed renderer>",
					Mesh?.Name ?? "<unnamed mesh>",
					result,
					detail);
			}

			return ready;
		}
	}
}
