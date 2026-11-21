namespace XREngine.Cutscenes;

/// <summary>The state of the shader programs of one T0 draw (a material on a renderer) during a cutscene open.</summary>
public enum ECutsceneProgramState
{
    /// <summary>
    /// The work did not start, or the backend cannot know the state yet. For example, no frame drew the renderer, so its
    /// pipeline variant is not known. The open waits.
    /// </summary>
    NotStarted,

    /// <summary>The variant build, the program link, or the pipeline compile runs. The open waits.</summary>
    Compiling,

    /// <summary>The draw does not wait for a build, a link, or a compile on the active backend.</summary>
    Ready,

    /// <summary>The work failed. The open reports a diagnostic and does not hide the failure.</summary>
    Failed,

    /// <summary>
    /// The active backend does not report the state. The open reports a diagnostic and never treats it as ready.
    /// </summary>
    NotSupported,
}
