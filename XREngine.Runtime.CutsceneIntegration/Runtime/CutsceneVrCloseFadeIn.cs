namespace XREngine.Cutscenes;

/// <summary>
/// The fade-in after the close of a <see cref="ECutsceneVrCameraMode.RigAnchor"/> cutscene. It keeps the headset black until
/// some render frames pass or a deadline passes, then fades in and resets the eye exposure, once.
/// <para>
/// A host runs <see cref="RenderStep"/> once in each render pass and <see cref="UpdateStep"/> once in each update. The render step
/// ends the hold after the render frames. The update step ends it at the deadline, so a backlog of render jobs cannot keep the
/// headset black past the deadline. The first step that runs the fade-in wins; the other step then does nothing.
/// </para>
/// <para>
/// The step owns the headset fade only while <see cref="ICutsceneVrHost.FadeGeneration"/> has the value that the close recorded.
/// A later fade call (for example the fade to black of the next cutscene) changes the generation, and then the step does nothing:
/// no fade-in and no exposure reset.
/// </para>
/// </summary>
internal sealed class CutsceneVrCloseFadeIn
{
    private readonly ICutsceneVrHost _host;
    private readonly long _generation;
    private readonly float _fadeSeconds;
    private readonly int _renderFrames;
    private readonly ulong _startFrame;
    private readonly double _deadlineSeconds;
    private int _done;

    /// <summary>Creates the step. It records the start frame and the deadline from the host now.</summary>
    /// <param name="host">The host that fades the headset.</param>
    /// <param name="generation">The fade generation after the fade to black of the close.</param>
    /// <param name="fadeSeconds">The fade-in time in seconds.</param>
    /// <param name="renderFrames">The minimum number of render frames that the headset stays black.</param>
    /// <param name="maximumSeconds">The maximum time in seconds that the headset stays black.</param>
    public CutsceneVrCloseFadeIn(ICutsceneVrHost host, long generation, float fadeSeconds, int renderFrames, double maximumSeconds)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _generation = generation;
        _fadeSeconds = fadeSeconds;
        _renderFrames = renderFrames;
        _startFrame = host.RenderFrameId;
        _deadlineSeconds = host.TimeSeconds + maximumSeconds;
    }

    /// <summary>True after a step ran the fade-in, or found that another fade owns the headset.</summary>
    public bool IsDone => Volatile.Read(ref _done) != 0;

    /// <summary>True if a step started the fade-in.</summary>
    public bool FadedIn { get; private set; }

    /// <summary>The render-side step. It ends the hold after the render frames or at the deadline.</summary>
    /// <returns>True when the step has no more work.</returns>
    public bool RenderStep()
    {
        if (IsDone)
            return true;
        if (_host.FadeGeneration != _generation)
            return Finish();
        if (_host.RenderFrameId - _startFrame < (ulong)_renderFrames && _host.TimeSeconds < _deadlineSeconds)
            return false;

        return RunFadeIn();
    }

    /// <summary>The update-side step. It ends the hold at the deadline.</summary>
    /// <returns>True when the step has no more work.</returns>
    public bool UpdateStep()
    {
        if (IsDone)
            return true;
        if (_host.FadeGeneration != _generation)
            return Finish();
        if (_host.TimeSeconds < _deadlineSeconds)
            return false;

        return RunFadeIn();
    }

    private bool Finish()
    {
        Interlocked.Exchange(ref _done, 1);
        return true;
    }

    private bool RunFadeIn()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0)
            return true;

        // The fade and the generation check are one host operation, so a fade to black of another cutscene is never reversed.
        if (!_host.TryFadeHeadsetIfGeneration(_generation, _fadeSeconds, 0.0f))
            return true;

        FadedIn = true;

        // The frames from the cutscene pose are done, so no eye frame of the old place can use the reset.
        _host.ResetEyeExposure();
        return true;
    }
}
