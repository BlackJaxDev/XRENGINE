using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	/// <summary>The largest number of pass contexts that the admission record keeps before it starts again.</summary>
	private const int MaxPipelineAdmissionContexts = 64;

	/// <summary>
	/// Protects <see cref="_pipelineAdmissions"/>. The pipeline step runs in the pre-recording admission and in draw
	/// recording, and not every recording path holds the recording lock, so the record has its own short lock.
	/// </summary>
	private readonly Lock _pipelineAdmissionSync = new();

	/// <summary>The result of the last graphics pipeline step of this renderer in each pass context.</summary>
	private readonly Dictionary<int, PipelineAdmissionRecord> _pipelineAdmissions = [];
	private string _pipelineAdmissionLastNotReadyReason = string.Empty;

	/// <summary>
	/// Gives the draw readiness of this renderer with a material: the program, the buffers, and the graphics pipelines that
	/// the frame loop resolved for its draws with that material.
	/// </summary>
	/// <remarks>
	/// The program and buffer step is the same non-blocking preparation as <see cref="TryPrepareForRendering(out string)"/>.
	/// A graphics pipeline depends on the render pass, the attachments, and the draw state of each pass, so only the frame
	/// loop knows the variant that a draw uses. This check does not compile a pipeline. It reads the results that the
	/// pipeline step of the frame loop recorded for this renderer (<see cref="EnsurePipelineCore"/>, from the pre-recording
	/// admission, which starts the asynchronous compile, and from draw recording). Only results with the same material and
	/// the current program count. A renderer that no frame drew with them gives <see cref="ERenderDrawReadiness.NotStarted"/>.
	/// </remarks>
	public ERenderDrawReadiness QueryDrawReadiness(XRMaterial? material, out string reason)
	{
		XRMaterial resolvedMaterial = material ?? ResolveMaterial(null, 1u);
		if (!TryPrepareForDrawEnqueue(resolvedMaterial, false, out string prepareReason))
		{
			reason = prepareReason;
			return prepareReason is "GenerateFailed" or "DataMissing" or "MaterialMissing"
				? ERenderDrawReadiness.Failed
				: ERenderDrawReadiness.Compiling;
		}

		lock (_pipelineAdmissionSync)
			return ReadPipelineAdmissions(resolvedMaterial, out reason);
	}

	/// <summary>Reads the pipeline records of a material. Call it with <see cref="_pipelineAdmissionSync"/> held.</summary>
	private ERenderDrawReadiness ReadPipelineAdmissions(XRMaterial material, out string reason)
	{
		VkRenderProgram? program = _program;
		ulong linkGeneration = program?.LinkGeneration ?? 0UL;
		bool anyAdmitted = false;
		bool anyPending = false;
		foreach (KeyValuePair<int, PipelineAdmissionRecord> entry in _pipelineAdmissions)
		{
			PipelineAdmissionRecord record = entry.Value;
			if (!ReferenceEquals(record.Material, material) ||
				!ReferenceEquals(record.Program, program) ||
				record.LinkGeneration != linkGeneration)
				continue;

			anyAdmitted = true;
			if (record.State == ERenderDrawReadiness.Failed)
			{
				reason = _pipelineAdmissionLastNotReadyReason;
				return ERenderDrawReadiness.Failed;
			}

			if (record.State != ERenderDrawReadiness.Ready)
				anyPending = true;
		}

		if (!anyAdmitted)
		{
			reason = "no frame drew this renderer with this material and its current program, so its pipeline variant is not known yet";
			return ERenderDrawReadiness.NotStarted;
		}

		if (anyPending)
		{
			reason = _pipelineAdmissionLastNotReadyReason;
			return ERenderDrawReadiness.Compiling;
		}

		reason = "Ready";
		return ERenderDrawReadiness.Ready;
	}

	/// <summary>
	/// Records the result of one graphics pipeline step of this renderer, after the step, so <see cref="_program"/> is the
	/// program of the draw. It runs for each draw, so it does not allocate: the record is a struct in a dictionary whose
	/// size is bounded by the pass contexts of the renderer.
	/// </summary>
	private void RecordPipelineAdmission(
		XRMaterial material,
		PrimitiveTopology topology,
		RenderPass renderPass,
		bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		int passIndex,
		string pipelineName,
		bool ready,
		bool retryable,
		string reason)
	{
		int contextKey = HashCode.Combine(
			passIndex,
			topology,
			renderPass.Handle,
			useDynamicRendering,
			dynamicRenderingFormats,
			pipelineName,
			RuntimeHelpers.GetHashCode(material));
		ERenderDrawReadiness state = ready
			? ERenderDrawReadiness.Ready
			: retryable ? ERenderDrawReadiness.Compiling : ERenderDrawReadiness.Failed;
		VkRenderProgram? program = _program;
		PipelineAdmissionRecord record = new(material, program, program?.LinkGeneration ?? 0UL, state);

		lock (_pipelineAdmissionSync)
		{
			if (_pipelineAdmissions.TryGetValue(contextKey, out PipelineAdmissionRecord previous) && previous == record)
				return;

			if (_pipelineAdmissions.Count >= MaxPipelineAdmissionContexts && !_pipelineAdmissions.ContainsKey(contextKey))
				_pipelineAdmissions.Clear();

			_pipelineAdmissions[contextKey] = record;
			if (!ready)
				_pipelineAdmissionLastNotReadyReason = reason;
		}
	}
}
