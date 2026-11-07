using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	private const string PrepareResultProgramsPending = "ProgramsPending";

	/// <summary>
	/// The material whose program this renderer cannot make (no compatible vertex shader), or null. A retry cannot clear
	/// it, so the draw readiness query gives <see cref="ERenderDrawReadiness.Failed"/>.
	/// </summary>
	private XRMaterial? _programFailureMaterial;

	/// <summary>
	/// Gives the draw readiness of this renderer with a material in the passes of a view: the program, the buffers, and the
	/// graphics pipelines of every required pass context of the view.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The program and buffer step is the same non-blocking preparation as <see cref="TryPrepareForRendering(out string)"/>,
	/// under the recording lock. A program whose backend build failed, or a material with no compatible vertex shader,
	/// gives <see cref="ERenderDrawReadiness.Failed"/>.
	/// </para>
	/// <para>
	/// A graphics pipeline depends on the render pass, the attachments, and the draw state of each pass, so only the frame
	/// loop knows the pass contexts. The query registers a request in <see cref="VulkanDrawReadinessRegistry"/> and gives
	/// the result of the last frame: the pre-recording admission prewarms the pipelines of this renderer and material for
	/// every non-shadow pass context of the view that draws the pass of the material, also when no view draws the renderer
	/// yet, and reads the batch or mesh-task pipelines on GPU-driven routes. The result is ready only when every required
	/// context is ready. The result follows each frame, so a program or pipeline change makes it not ready again.
	/// </para>
	/// </remarks>
	public ERenderDrawReadiness QueryDrawReadiness(in RenderDrawReadinessQuery query, out string reason)
	{
		XRMaterial resolvedMaterial = query.Material ?? ResolveMaterial(null, 1u);
		long nowTicks = Environment.TickCount64;
		ERenderDrawReadiness pipelineState = BackendContext.Resources.DrawReadiness.Observe(
			this,
			resolvedMaterial,
			query.View,
			query.Stereo,
			nowTicks,
			out string pipelineReason);

		if (!_recordDrawSync.TryEnter())
		{
			reason = "renderer recording state is busy";
			return ERenderDrawReadiness.Compiling;
		}

		bool prepared;
		string prepareReason;
		try
		{
			prepared = TryPrepareForDrawEnqueue(resolvedMaterial, false, out prepareReason);
		}
		finally
		{
			_recordDrawSync.Exit();
		}

		if (!prepared)
			return ClassifyPrepareFailure(resolvedMaterial, prepareReason, out reason);

		reason = pipelineReason;
		return pipelineState;
	}

	private ERenderDrawReadiness ClassifyPrepareFailure(XRMaterial material, string prepareReason, out string reason)
	{
		if (string.Equals(prepareReason, PrepareResultProgramsPending, StringComparison.Ordinal))
		{
			if (ReferenceEquals(_programFailureMaterial, material))
			{
				reason = "no compatible vertex shader for this material";
				return ERenderDrawReadiness.Failed;
			}

			if (_program?.Data.ShaderMetadata.Backend is { Stage: EShaderProgramBackendStage.Failed } status)
			{
				reason = status.FailureReason ?? "the Vulkan program build failed";
				return ERenderDrawReadiness.Failed;
			}
		}

		reason = prepareReason;
		return ClassifyPrepareResult(prepareReason);
	}

	/// <summary>Maps a not-ready preparation result to a draw readiness state.</summary>
	internal static ERenderDrawReadiness ClassifyPrepareResult(string prepareResult)
		=> prepareResult is "DataMissing" or "MaterialMissing"
			? ERenderDrawReadiness.Failed
			: ERenderDrawReadiness.Compiling;
}
