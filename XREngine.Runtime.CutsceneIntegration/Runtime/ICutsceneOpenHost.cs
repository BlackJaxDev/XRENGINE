namespace XREngine.Cutscenes;

/// <summary>
/// An owner of an open cutscene that a <see cref="CutsceneOpenSequence"/> can open: the play-mode
/// <see cref="CutsceneDirectorComponent"/> or the editor preview host. All members run on the update thread.
/// </summary>
/// <remarks>
/// The host calls <see cref="CutsceneOpenSequence.OnHostFrame"/> at the end of each frame, after the residency update of
/// that frame, while a sequence is attached. It calls <see cref="CutsceneOpenSequence.OnHostClosed"/> when it closes the
/// cutscene for any other reason than <see cref="CloseForSequence"/>.
/// </remarks>
public interface ICutsceneOpenHost
{
    /// <summary>Opens the cutscene. Returns true if it is open. A cancel throws <see cref="OperationCanceledException"/>.</summary>
    /// <param name="cancellationToken">A token that cancels the open.</param>
    bool OpenForSequence(CancellationToken cancellationToken);

    /// <summary>The player of the open cutscene, or null when it is closed. A preview recompile replaces it.</summary>
    CutscenePlayer? OpenPlayer { get; }

    /// <summary>The residency session of the open cutscene, or null when residency does not run.</summary>
    CutsceneResidencySession? OpenResidency { get; }

    /// <summary>The binding table of the open cutscene, or null.</summary>
    CutsceneBindingTable? OpenBindings { get; }

    /// <summary>
    /// The desktop viewport that shows the cutscene camera, or null when no viewport shows it. The open measures the T0 draw
    /// readiness against the render passes of this viewport, so another view or a shadow pass does not decide it.
    /// </summary>
    XREngine.Rendering.XRViewport? OpenViewport => null;

    /// <summary>Connects a sequence, so that it advances at the end of each frame.</summary>
    /// <param name="sequence">The sequence.</param>
    void AttachOpenSequence(CutsceneOpenSequence sequence);

    /// <summary>Disconnects a sequence if it is the connected one.</summary>
    /// <param name="sequence">The sequence.</param>
    void DetachOpenSequence(CutsceneOpenSequence sequence);

    /// <summary>Closes the cutscene after a cancel and restores the scene. The connected sequence is already disconnected.</summary>
    void CloseForSequence();

    /// <summary>Adds a problem that the sequence found to the diagnostics of the host.</summary>
    /// <param name="message">The message.</param>
    void AddOpenDiagnostic(string message);
}
