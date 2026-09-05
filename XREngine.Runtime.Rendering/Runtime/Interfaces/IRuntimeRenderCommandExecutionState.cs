namespace XREngine.Rendering;

/// <summary>
/// Active render-command state published by the host while command chains are collecting or rendering.
/// </summary>
public interface IRuntimeRenderCommandExecutionState
{
    /// <summary>
    /// Gets the window viewport currently targeted by render command execution.
    /// </summary>
    IRuntimeViewportHost? WindowViewport { get; }

    /// <summary>
    /// Gets the scene command context currently rendering, if command execution is inside a scene pass.
    /// </summary>
    IRuntimeRenderCommandSceneContext? RenderingScene { get; }

    /// <summary>
    /// Gets the scene camera selected for the active command execution state.
    /// </summary>
    IRuntimeRenderCamera? SceneCamera { get; }

    /// <summary>
    /// Gets the camera currently used for draw submission, including capture and stereo overrides.
    /// </summary>
    IRuntimeRenderCamera? RenderingCamera { get; }

    /// <summary>
    /// Gets the right-eye camera paired with <see cref="RenderingCamera"/> during stereo rendering.
    /// </summary>
    IRuntimeRenderCamera? StereoRightEyeCamera { get; }

    /// <summary>
    /// Gets whether render command execution is currently producing a stereo pass.
    /// </summary>
    bool StereoPass { get; }

    /// <summary>
    /// Gets whether command execution is producing a shadow pass.
    /// </summary>
    bool ShadowPass => false;

    /// <summary>
    /// Gets the output-local sequence captured for desktop temporal history.
    /// Zero means this invocation has no desktop history ownership.
    /// </summary>
    ulong ViewHistorySequenceId => 0UL;

    /// <summary>
    /// Gets the pipeline and resource-generation identity captured with desktop view history.
    /// </summary>
    ulong ViewHistoryPipelineIdentity => 0UL;

    /// <summary>True when this capture belongs to command-chain authoring rather than visibility collection.</summary>
    bool ViewHistoryAuthoring => false;

    /// <summary>
    /// Gets the immutable logical views captured before visibility generation for this render invocation.
    /// </summary>
    RenderFrameViewSet? FrameViewSet => null;

    /// <summary>
    /// Gets the frame-owned immutable publication of scene buffers and views.
    /// </summary>
    RenderWorldSnapshot? WorldSnapshot => null;
}
