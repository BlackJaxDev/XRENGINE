namespace XREngine.Rendering;

/// <summary>
/// The state of the backend objects that one mesh renderer needs to draw with one material: the program link and, on
/// backends that use them, the graphics pipelines. <see cref="IRenderPreparationState.QueryDrawReadiness"/> gives it.
/// </summary>
public enum ERenderDrawReadiness
{
    /// <summary>
    /// The backend did not start the work, or it cannot know the state yet. For example, no frame admitted a draw of the
    /// renderer, so the pipeline variant of its draw is not known.
    /// </summary>
    NotStarted,

    /// <summary>The backend works on the program link, the pipeline compile, or the buffers. Check again later.</summary>
    Compiling,

    /// <summary>The renderer can draw with the material now. A draw does not wait for a link or a compile.</summary>
    Ready,

    /// <summary>The work failed, and a retry cannot clear it. The renderer cannot draw with the material.</summary>
    Failed,

    /// <summary>
    /// The backend does not report the state. A caller must not treat this as ready: it shows a diagnostic.
    /// </summary>
    NotSupported,
}
