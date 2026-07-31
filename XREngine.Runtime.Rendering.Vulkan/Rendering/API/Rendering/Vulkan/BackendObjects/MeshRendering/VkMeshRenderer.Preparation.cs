using System;
using System.Collections.Generic;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
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

		if (!TryEnsureDescriptorSetsForPreparation(
				material,
				0,
				bindingSnapshot: null,
				out string descriptorDetail))
			return SetPrepareResult(false, "DescriptorsPending", descriptorDetail, out reason);

		return SetPrepareResult(true, "Ready", BuildPrepareSuccessDetail("Ready"), out reason);
	}

	/// <summary>
	/// Prepares only the immutable program and geometry resources required to
	/// capture a deferred draw. Descriptor publication, scoped material/light
	/// bindings, and vertex-input construction belong to command recording,
	/// where the captured program and binding snapshot are authoritative.
	/// </summary>
	private bool TryPrepareForDrawEnqueue(XRMaterial material, out string reason)
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

		bool materialChanged = !ReferenceEquals(_lastPreparedMaterial, material);
		if (materialChanged)
		{
			_pipelineDirty = true;
			_descriptorDirty = true;
			_lastPreparedMaterial = material;
		}

		if (MeshRenderer.HasRenderDataPreparation)
			MeshRenderer.OnPreparingRenderData();

		EnsureRuntimeDeformationBuffersCurrent();

		bool shaderConfigurationChanged =
			_pipelineShaderConfigVersion != RuntimeEngine.Rendering.Settings.ShaderConfigVersion ||
			_pipelineUsesShaderClipDepthRemap != RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap ||
			_pipelineUsesNativeDepthClipControl != RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl;
		bool canReuseProgramAndBuffers =
			!materialChanged &&
			_program is not null &&
			_activeProgramLinkGeneration == _program.LinkGeneration &&
			!_buffersDirty &&
			!shaderConfigurationChanged;
		if (canReuseProgramAndBuffers &&
			AreCachedBuffersReadyForRendering(out _, ProgramUsesShaderGeneratedVertices()))
			return true;

		if (!EnsureProgram(material))
			return SetPrepareResult(false, "ProgramsPending", "No compatible Vulkan render program is available yet.", out reason);

		bool usesShaderGeneratedVertices = ProgramUsesShaderGeneratedVertices();
		EnsureBuffers(usesShaderGeneratedVertices);

		if (!AreCachedBuffersReadyForRendering(out string bufferDetail, usesShaderGeneratedVertices))
			return SetPrepareResult(false, "BuffersPending", bufferDetail, out reason);

		return SetPrepareResult(true, "Ready", BuildPrepareSuccessDetail("DeferredUntilRecording"), out reason);
	}

	private bool TryPrepareCapturedProgramForRecording(
		XRMaterial material,
		VkRenderProgram preparedProgram,
		string? preparedProgramIdentity,
		ulong preparedProgramLinkGeneration,
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

		if (!ActivateCapturedProgram(
				material,
				preparedProgram,
				preparedProgramIdentity,
				preparedProgramLinkGeneration))
			return SetPrepareResult(false, "ProgramsPending", "The captured Vulkan program is being relinked.", out reason);

		EnsureRuntimeDeformationBuffersCurrent();
		if (CanReuseCapturedPreparedRenderState(material, preparedProgram, preparedProgramIdentity))
		{
			ApplyScopedProgramBindingsForPreparation(material);
			if (programBindingSnapshot is not null)
				_program?.ApplyBindingSnapshot(programBindingSnapshot);

			if (!TryEnsureDescriptorSetsForPreparation(
					material,
					drawUniformSlot,
					programBindingSnapshot,
					out string reuseDescriptorDetail))
				return SetPrepareResult(false, "DescriptorsPending", reuseDescriptorDetail, out reason);

			return SetPrepareResult(true, "Ready", BuildPrepareSuccessDetail("Deferred"), out reason);
		}

		bool usesShaderGeneratedVertices = ProgramUsesShaderGeneratedVertices();
		EnsureBuffers(usesShaderGeneratedVertices);

		if (!AreCachedBuffersReadyForRendering(out string bufferDetail, usesShaderGeneratedVertices))
			return SetPrepareResult(false, "BuffersPending", bufferDetail, out reason);

		ApplyScopedProgramBindingsForPreparation(material);
		if (programBindingSnapshot is not null)
			_program?.ApplyBindingSnapshot(programBindingSnapshot);
		BuildVertexInputState();

		if (!TryEnsureDescriptorSetsForPreparation(
				material,
				drawUniformSlot,
				programBindingSnapshot,
				out string descriptorDetail))
			return SetPrepareResult(false, "DescriptorsPending", descriptorDetail, out reason);

		return SetPrepareResult(true, "Ready", BuildPrepareSuccessDetail("Ready"), out reason);
	}

	private bool TryReuseCapturedProgramForIndirectDrawSnapshot(
		XRMaterial material,
		VkRenderProgram preparedProgram,
		string? preparedProgramIdentity,
		ulong preparedProgramLinkGeneration,
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

		if (!ActivateCapturedProgram(
				material,
				preparedProgram,
				preparedProgramIdentity,
				preparedProgramLinkGeneration))
			return SetPrepareResult(false, "ProgramsPending", "The captured Vulkan program is being relinked.", out reason);

		EnsureRuntimeDeformationBuffersCurrent();
		bool usesShaderGeneratedVertices = ProgramUsesShaderGeneratedVertices();
		EnsureBuffers(usesShaderGeneratedVertices);

		if (!AreCachedBuffersReadyForRendering(out string bufferDetail, usesShaderGeneratedVertices))
			return SetPrepareResult(false, "BuffersPending", bufferDetail, out reason);

		ApplyScopedProgramBindingsForPreparation(material);
		if (programBindingSnapshot is not null)
			_program?.ApplyBindingSnapshot(programBindingSnapshot);
		BuildVertexInputState();

		if (!CanReuseRecordedDescriptorSets(
				material,
				drawUniformSlot,
				programBindingSnapshot is not null,
				programBindingSnapshot,
				out string descriptorReason))
		{
			// This method is an allocation-free probe used immediately before the
			// legal prewarm fallback. A cache miss is not a failed draw and must not
			// publish a renderer-not-ready result (or its rate-limited stack trace).
			// The fallback owns the authoritative preparation result.
			reason = $"Descriptor sets are not prewarmed for the captured indirect draw layout: {descriptorReason}";
			return false;
		}

		return SetPrepareResult(true, "Ready", BuildPrepareSuccessDetail("Reused"), out reason);
	}

	private bool ActivateCapturedProgram(
		XRMaterial material,
		VkRenderProgram preparedProgram,
		string? preparedProgramIdentity,
		ulong preparedProgramLinkGeneration)
	{
		if (preparedProgram.LinkGeneration != preparedProgramLinkGeneration)
		{
			Renderer.MarkCommandBuffersDirtyForLegacyMeshState();
			return false;
		}

		string? identity = preparedProgramIdentity ?? preparedProgram.Data?.Name;
		bool replacingProgram =
			_program is not null &&
			!ReferenceEquals(_program, preparedProgram);
		if (replacingProgram ||
			_program is null ||
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
		_program.Generate();
		if (!_program.Link(MeshRenderer?.GenerateAsync ?? false))
			return false;
		if (_program.LinkGeneration != preparedProgramLinkGeneration)
		{
			Renderer.MarkCommandBuffersDirtyForLegacyMeshState();
			return false;
		}

		ObserveActiveProgramLinkGeneration(_program, replacingProgram);
		return true;
	}

	/// <summary>
	/// Invalidates every mesh-local object whose compatibility depends on a
	/// program interface when that interface is rebuilt in place or its backend
	/// wrapper is replaced.
	/// </summary>
	private void ObserveActiveProgramLinkGeneration(
		VkRenderProgram program,
		bool replacingProgram = false)
	{
		ulong linkGeneration = program.LinkGeneration;
		if (!replacingProgram &&
			_activeProgramLinkGeneration == linkGeneration)
			return;

		bool replacingLinkedInterface =
			replacingProgram ||
			_activeProgramLinkGeneration != 0;
		_activeProgramLinkGeneration = linkGeneration;
		if (replacingLinkedInterface)
		{
			// Pipeline keys and prepared records carry the link generation, but
			// mesh-local descriptor/payload tables also retain reflected block
			// identities. Drop only this renderer's interface-dependent state so
			// a relink cannot reuse stale bindings or grow one payload set per
			// historical shader interface.
			_pipelines.Clear();
			ReleaseDescriptorAllocation();
			DestroyEngineUniformBuffers();
			DestroyAutoUniformBuffers();
		}
		_pipelineDirty = true;
		_descriptorDirty = true;
		_vertexInputStateDirty = true;
	}

	private bool CanReusePreparedRenderState(XRMaterial material)
	{
		if (!ReferenceEquals(_lastPreparedMaterial, material) ||
			_program is null ||
			_activeProgramLinkGeneration != _program.LinkGeneration ||
			_pipelineDirty ||
			_buffersDirty ||
			_descriptorDirty ||
			!string.Equals(_lastPrepareResult, "Ready", StringComparison.Ordinal))
			return false;

		if (_pipelineShaderConfigVersion != RuntimeEngine.Rendering.Settings.ShaderConfigVersion ||
			_pipelineUsesShaderClipDepthRemap != RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap ||
			_pipelineUsesNativeDepthClipControl != RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl)
			return false;

		return AreCachedBuffersReadyForRendering(out _, ProgramUsesShaderGeneratedVertices());
	}

	private bool CanReuseCapturedPreparedRenderState(XRMaterial material, VkRenderProgram preparedProgram, string? preparedProgramIdentity)
	{
		string? identity = preparedProgramIdentity ?? preparedProgram.Data?.Name;
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
		if ((reqs & EUniformRequirements.Lights) != 0)
		{
			RuntimeEngine.Rendering.State.RenderingWorld?.Lights?.SetForwardLightingUniforms(program);
			lightingUniformsBound = RuntimeEngine.Rendering.State.RenderingWorld?.Lights is not null;
		}

		if ((reqs & EUniformRequirements.AmbientOcclusion) != 0 && !lightingUniformsBound)
			Lights3DCollection.SetForwardAmbientOcclusionUniforms(program);

		if (!RuntimeEngine.Rendering.State.IsShadowPass)
			RuntimeEngine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(program);
	}

	private bool ProgramUsesShaderGeneratedVertices()
		=> _program is not null &&
		   _program.TryGetVertexStageInputCount(out int vertexInputCount) &&
		   vertexInputCount == 0;

	private bool TryEnsureDescriptorSetsForPreparation(
		XRMaterial material,
		int drawUniformSlot,
		ComputeDispatchSnapshot? bindingSnapshot,
		out string detail)
	{
		try
		{
			if (EnsureDescriptorSets(
					material,
					drawUniformSlot,
					bindingSnapshot: bindingSnapshot))
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
		BufferReadinessSnapshot snapshot = Volatile.Read(ref _bufferReadinessSnapshot);
		if (!string.IsNullOrEmpty(snapshot.MissingExpectedIndexBufferDetail))
		{
			detail = snapshot.MissingExpectedIndexBufferDetail;
			return false;
		}

		KeyValuePair<string, VkDataBuffer>[] buffers = skipVertexAttributeBuffers
			? snapshot.ShaderGeneratedRequiredBuffers
			: snapshot.RequiredBuffers;
		for (int i = 0; i < buffers.Length; i++)
		{
			KeyValuePair<string, VkDataBuffer> pair = buffers[i];
			VkDataBuffer buffer = pair.Value;
			if (!buffer.IsReadyForRendering)
			{
				detail = $"buffer='{pair.Key}' target={buffer.Data.Target} generated={buffer.IsGenerated} length={buffer.Data.Length} allocated={buffer.AllocatedByteSize}";
				return false;
			}
		}

		detail = string.Empty;
		return true;
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
		if (!VulkanRenderer.CommandRecordingDiagnosticsEnabled)
			return string.Empty;

		return $"buffers=Ready; program={_program?.Data?.Name ?? "<unnamed>"}; descriptors={descriptorState}; pipeline=DeferredUntilPass; layout={_geometryLayoutSignature.DebugSummary}";
	}
}
