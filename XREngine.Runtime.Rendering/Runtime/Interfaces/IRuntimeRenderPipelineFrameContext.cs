namespace XREngine.Rendering;

/// <summary>
/// Frame-scoped pipeline state used by render commands, diagnostics, and backend bridge code.
/// </summary>
public interface IRuntimeRenderPipelineFrameContext : IRuntimeRenderPipelineDebugContext
{
    /// <summary>
    /// Gets the concrete pipeline host object when runtime code needs to pass it back to host-owned APIs.
    /// </summary>
    IRuntimeRenderPipelineHost? PipelineHost { get; }

    /// <summary>
    /// Gets the render-command execution state active for this pipeline frame.
    /// </summary>
    IRuntimeRenderCommandExecutionState RenderState { get; }

    /// <summary>
    /// Gets the most recent scene camera selected by the pipeline for scene collection.
    /// </summary>
    IRuntimeRenderCamera? LastSceneCamera { get; }

    /// <summary>
    /// Gets the most recent camera actually used for rendering, after overrides and stereo selection.
    /// </summary>
    IRuntimeRenderCamera? LastRenderingCamera { get; }

    /// <summary>
    /// Gets the most recent window viewport associated with the pipeline frame.
    /// </summary>
    IRuntimeViewportHost? LastWindowViewport { get; }

    /// <summary>
    /// Gets the output HDR decision resolved for the current frame, or <see langword="null"/> before resolution.
    /// </summary>
    bool? EffectiveOutputHDRThisFrame { get; }

    /// <summary>
    /// Gets the anti-aliasing mode resolved for the current frame, or <see langword="null"/> before resolution.
    /// </summary>
    EAntiAliasingMode? EffectiveAntiAliasingModeThisFrame { get; }

    /// <summary>
    /// Gets the MSAA sample count resolved for the current frame, or <see langword="null"/> before resolution.
    /// </summary>
    uint? EffectiveMsaaSampleCountThisFrame { get; }

    /// <summary>
    /// Gets the TSR render scale resolved for the current frame, or <see langword="null"/> before resolution.
    /// </summary>
    float? EffectiveTsrRenderScaleThisFrame { get; }
}
