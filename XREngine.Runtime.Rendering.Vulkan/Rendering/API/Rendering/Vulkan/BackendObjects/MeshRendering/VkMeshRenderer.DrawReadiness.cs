using System.Threading;
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

	/// <summary>True after <see cref="DeleteObjectInternal"/> until the wrapper is made again.</summary>
	private bool _drawReadinessDeleted;

	/// <summary>
	/// True when the readiness prewarm may use this wrapper: it is active, linked to its data, not retired, and not deleted.
	/// The prewarm never makes programs or pipelines for a wrapper that is not.
	/// </summary>
	internal bool CanServeDrawReadiness
		=> IsActive && _isDataLinked && !IsRetired && !Volatile.Read(ref _drawReadinessDeleted);

	/// <summary>
	/// Gives the draw readiness of this renderer with a material in the passes of a view: the program, the buffers, and the
	/// graphics pipelines of every required pass context of the view.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The program and buffer step is the same non-blocking preparation as <see cref="TryPrepareForRendering(out string)"/>,
	/// under the recording lock. A program whose backend build failed, or a material with no compatible vertex shader,
	/// gives <see cref="ERenderDrawReadiness.Failed"/>. A retired or deleted wrapper does no work and gives
	/// <see cref="ERenderDrawReadiness.NotStarted"/>.
	/// </para>
	/// <para>
	/// A graphics pipeline depends on the render pass, the attachments, and the draw state of each pass, so only the frame
	/// loop knows the pass contexts. The query registers a request in <see cref="VulkanDrawReadinessRegistry"/> and gives
	/// the result of the last frame: the pre-recording admission prewarms the pipelines of this renderer and material for
	/// every non-shadow pass context of the view that draws the pass of the material, also when no view draws the renderer
	/// yet, and reads the batch or mesh-task pipelines on GPU-driven routes. The result is ready only when every required
	/// context is ready, and only for the program and link generation that the frame measured: after a program swap or a
	/// relink, a ready result is <see cref="ERenderDrawReadiness.Compiling"/> until the next frame measures the new program.
	/// </para>
	/// </remarks>
	public ERenderDrawReadiness QueryDrawReadiness(in RenderDrawReadinessQuery query, out string reason)
	{
		if (IsRetired || Volatile.Read(ref _drawReadinessDeleted))
		{
			BackendContext.Resources.DrawReadiness.RemoveRenderer(this);
			reason = "the renderer wrapper is retired or deleted";
			return ERenderDrawReadiness.NotStarted;
		}

		XRMaterial resolvedMaterial = query.Material ?? ResolveMaterial(null, 1u);
		if (!_recordDrawSync.TryEnter())
		{
			reason = "renderer recording state is busy";
			return ERenderDrawReadiness.Compiling;
		}

		bool prepared;
		string prepareReason;
		VkRenderProgram? program;
		try
		{
			prepared = TryPrepareForDrawEnqueue(resolvedMaterial, false, out prepareReason);
			program = _program;
		}
		finally
		{
			_recordDrawSync.Exit();
		}

		ERenderDrawReadiness publishedState = BackendContext.Resources.DrawReadiness.Observe(
			this,
			resolvedMaterial,
			query.RenderOptionsOverride,
			query.View,
			query.Stereo,
			Environment.TickCount64,
			out string publishedReason,
			out object? publishedProgram,
			out ulong publishedLinkGeneration);

		if (!prepared)
			return ClassifyPrepareFailure(resolvedMaterial, prepareReason, out reason);

		return VulkanDrawReadinessRegistry.ResolvePublishedState(
			publishedState,
			publishedReason,
			publishedProgram,
			publishedLinkGeneration,
			program,
			program?.LinkGeneration ?? 0UL,
			out reason);
	}

	/// <summary>Ends the readiness requests of this renderer. The open calls it through the facade when it ends.</summary>
	public void EndDrawReadinessQueries()
		=> BackendContext.Resources.DrawReadiness.RemoveRenderer(this);

	/// <summary>Gets the active program of this renderer and its link generation, for the readiness result.</summary>
	internal VkRenderProgram? ActiveProgramForReadiness => _program;

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
