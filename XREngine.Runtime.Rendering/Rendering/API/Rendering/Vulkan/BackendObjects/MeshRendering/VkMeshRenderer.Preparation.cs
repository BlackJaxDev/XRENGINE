using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

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
			   AreCachedBuffersReadyForRendering(out _, ProgramUsesShaderGeneratedVertices()) &&
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

			if (CanReusePreparedRenderState(material))
				return true;

			if (!EnsureProgram(material))
				return SetPrepareResult(false, "ProgramsPending", "No compatible Vulkan render program is available yet.", out reason);

			bool usesShaderGeneratedVertices = ProgramUsesShaderGeneratedVertices();
			EnsureBuffers(usesShaderGeneratedVertices);

			if (!AreCachedBuffersReadyForRendering(out string bufferDetail, usesShaderGeneratedVertices))
				return SetPrepareResult(false, "BuffersPending", bufferDetail, out reason);

			ApplyScopedProgramBindingsForPreparation(material);
			BuildVertexInputState();

			if (!TryEnsureDescriptorSetsForPreparation(material, 0, out string descriptorDetail))
				return SetPrepareResult(false, "DescriptorsPending", descriptorDetail, out reason);

			return SetPrepareResult(true, "Ready", BuildPrepareSuccessDetail("Ready"), out reason);
		}

		private bool TryPrepareCapturedProgramForRecording(
			XRMaterial material,
			VkRenderProgram preparedProgram,
			string? preparedProgramIdentity,
			ComputeDispatchSnapshot? programBindingSnapshot,
			int drawUniformSlot,
			out string reason)
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

			ActivateCapturedProgram(material, preparedProgram, preparedProgramIdentity);
			EnsureRuntimeDeformationBuffersCurrent();
			if (CanReuseCapturedPreparedRenderState(material, preparedProgram, preparedProgramIdentity))
				return SetPrepareResult(true, "Ready", BuildPrepareSuccessDetail("Deferred"), out reason);

			bool usesShaderGeneratedVertices = ProgramUsesShaderGeneratedVertices();
			EnsureBuffers(usesShaderGeneratedVertices);

			if (!AreCachedBuffersReadyForRendering(out string bufferDetail, usesShaderGeneratedVertices))
				return SetPrepareResult(false, "BuffersPending", bufferDetail, out reason);

			ApplyScopedProgramBindingsForPreparation(material);
			if (programBindingSnapshot is not null)
				_program?.ApplyBindingSnapshot(programBindingSnapshot);
			BuildVertexInputState();

			if (!TryEnsureDescriptorSetsForPreparation(material, drawUniformSlot, out string descriptorDetail))
				return SetPrepareResult(false, "DescriptorsPending", descriptorDetail, out reason);

			return SetPrepareResult(true, "Ready", BuildPrepareSuccessDetail("Ready"), out reason);
		}

		private bool TryReuseCapturedProgramForIndirectDrawSnapshot(
			XRMaterial material,
			VkRenderProgram preparedProgram,
			string? preparedProgramIdentity,
			ComputeDispatchSnapshot? programBindingSnapshot,
			int drawUniformSlot,
			out string reason)
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

			ActivateCapturedProgram(material, preparedProgram, preparedProgramIdentity);
			EnsureRuntimeDeformationBuffersCurrent();
			bool usesShaderGeneratedVertices = ProgramUsesShaderGeneratedVertices();
			EnsureBuffers(usesShaderGeneratedVertices);

			if (!AreCachedBuffersReadyForRendering(out string bufferDetail, usesShaderGeneratedVertices))
				return SetPrepareResult(false, "BuffersPending", bufferDetail, out reason);

			ApplyScopedProgramBindingsForPreparation(material);
			if (programBindingSnapshot is not null)
				_program?.ApplyBindingSnapshot(programBindingSnapshot);
			BuildVertexInputState();

			if (!CanReuseRecordedDescriptorSets(material, drawUniformSlot, programBindingSnapshot is not null, out string descriptorReason))
			{
				return SetPrepareResult(
					false,
					"DescriptorsPending",
					$"Descriptor sets are not prewarmed for the captured indirect draw layout: {descriptorReason}",
					out reason);
			}

			return SetPrepareResult(true, "Ready", BuildPrepareSuccessDetail("Reused"), out reason);
		}

		private void ActivateCapturedProgram(XRMaterial material, VkRenderProgram preparedProgram, string? preparedProgramIdentity)
		{
			string identity = preparedProgramIdentity ?? preparedProgram.Data?.Name ?? preparedProgram.GetHashCode().ToString();
			if (!ReferenceEquals(_program, preparedProgram) ||
				!string.Equals(_activeProgramIdentity, identity, StringComparison.Ordinal))
			{
				_pipelineDirty = true;
				_descriptorDirty = true;
				_vertexInputStateDirty = true;
				_activeProgramIdentity = identity;
			}

			if (!ReferenceEquals(_lastPreparedMaterial, material))
			{
				_pipelineDirty = true;
				_descriptorDirty = true;
				_lastPreparedMaterial = material;
			}

			_generatedProgram = preparedProgram.Data;
			_program = preparedProgram;
		}

		private bool CanReusePreparedRenderState(XRMaterial material)
		{
			if (!ReferenceEquals(_lastPreparedMaterial, material) ||
				_program is null ||
				_pipelineDirty ||
				_buffersDirty ||
				_descriptorDirty ||
				!string.Equals(_lastPrepareResult, "Ready", StringComparison.Ordinal))
			{
				return false;
			}

			if (_pipelineShaderConfigVersion != RuntimeEngine.Rendering.Settings.ShaderConfigVersion ||
				_pipelineUsesShaderClipDepthRemap != RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap ||
				_pipelineUsesNativeDepthClipControl != RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl)
			{
				return false;
			}

			return AreCachedBuffersReadyForRendering(out _, ProgramUsesShaderGeneratedVertices());
		}

		private bool CanReuseCapturedPreparedRenderState(XRMaterial material, VkRenderProgram preparedProgram, string? preparedProgramIdentity)
		{
			string identity = preparedProgramIdentity ?? preparedProgram.Data?.Name ?? preparedProgram.GetHashCode().ToString();
			return ReferenceEquals(_lastPreparedMaterial, material) &&
				ReferenceEquals(_program, preparedProgram) &&
				string.Equals(_activeProgramIdentity, identity, StringComparison.Ordinal) &&
				!_buffersDirty &&
				!_vertexInputStateDirty &&
				string.Equals(_lastPrepareResult, "Ready", StringComparison.Ordinal) &&
				AreCachedBuffersReadyForRendering(out _, ProgramUsesShaderGeneratedVertices());
		}

		private void ApplyScopedProgramBindingsForPreparation(XRMaterial material)
		{
			if (_program?.Data is not { } program)
				return;

			_program.ClearBindings();
			RuntimeEngine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(program);

			EUniformRequirements reqs =
				(material.RenderOptions?.RequiredEngineUniforms ?? EUniformRequirements.None) |
				program.GetActiveEngineUniformRequirements();

			bool lightingUniformsBound = false;
			if (reqs.HasFlag(EUniformRequirements.Lights))
			{
				RuntimeEngine.Rendering.State.RenderingWorld?.Lights?.SetForwardLightingUniforms(program);
				lightingUniformsBound = RuntimeEngine.Rendering.State.RenderingWorld?.Lights is not null;
			}

			if (reqs.HasFlag(EUniformRequirements.AmbientOcclusion) && !lightingUniformsBound)
				Lights3DCollection.SetForwardAmbientOcclusionUniforms(program);

			if (!RuntimeEngine.Rendering.State.IsShadowPass)
				RuntimeEngine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(program);
		}

		private bool ProgramUsesShaderGeneratedVertices()
			=> _program is not null &&
			   _program.TryGetVertexStageInputCount(out int vertexInputCount) &&
			   vertexInputCount == 0;

		private bool TryEnsureDescriptorSetsForPreparation(XRMaterial material, int drawUniformSlot, out string detail)
		{
			try
			{
				if (EnsureDescriptorSets(material, drawUniformSlot))
				{
					detail = string.Empty;
					return true;
				}

				detail = "Descriptor sets are not allocated or populated for the active program/material layout.";
				return false;
			}
			catch (VulkanOutOfMemoryException ex) when (VulkanRenderer.IsExpectedVulkanImageAllocationDeferral(ex))
			{
				detail = $"Descriptor resources deferred under Vulkan allocator pressure: {ex.Message}";
				return false;
			}
		}

		private bool AreCachedBuffersReadyForRendering(out string detail, bool skipVertexAttributeBuffers = false)
		{
			lock (_bufferStateSync)
			{
				foreach (var pair in _bufferCache)
				{
					VkDataBuffer buffer = pair.Value;
					if (skipVertexAttributeBuffers && buffer.Data.Target == EBufferTarget.ArrayBuffer)
						continue;

					if (!buffer.IsReadyForRendering)
					{
						detail = $"buffer='{pair.Key}' target={buffer.Data.Target} generated={buffer.IsGenerated} length={buffer.Data.Length} allocated={buffer.AllocatedByteSize}";
						return false;
					}
				}

				if (!_indexBuffersSkippedForShaderGeneratedVertices &&
					!IsExpectedIndexBufferReady(EPrimitiveType.Triangles, _triangleIndexBuffer, "Triangles", out detail))
					return false;
				if (!_indexBuffersSkippedForShaderGeneratedVertices &&
					!IsExpectedIndexBufferReady(EPrimitiveType.Lines, _lineIndexBuffer, "Lines", out detail))
					return false;
				if (!_indexBuffersSkippedForShaderGeneratedVertices &&
					!IsExpectedIndexBufferReady(EPrimitiveType.Points, _pointIndexBuffer, "Points", out detail))
					return false;

				detail = string.Empty;
				return true;
			}
		}

		private bool IsExpectedIndexBufferReady(EPrimitiveType type, VkDataBuffer? buffer, string primitiveName, out string detail)
		{
			if (Mesh?.HasIndexData(type) == true && buffer is null)
			{
				detail = $"indexBuffer='{primitiveName}' pending async build for indexed mesh";
				return false;
			}

			return IsIndexBufferReady(buffer, primitiveName, out detail);
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

		private string BuildPrepareSuccessDetail(string descriptorState)
		{
			if (!CommandRecordingDiagnosticsEnabled)
				return string.Empty;

			return $"buffers=Ready; program={_program?.Data?.Name ?? "<unnamed>"}; descriptors={descriptorState}; pipeline=DeferredUntilPass; layout={_geometryLayoutSignature.DebugSummary}";
		}
	}
}
