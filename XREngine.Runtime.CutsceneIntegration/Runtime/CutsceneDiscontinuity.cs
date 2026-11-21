namespace XREngine.Cutscenes;

/// <summary>
/// A jump of the cutscene position. Systems that keep history from frame to frame, for example
/// temporal anti-aliasing or audio sources, must reset or reseek when they receive this signal.
/// <see cref="Kind"/> tells which history the jump invalidates.
/// </summary>
/// <param name="From">The position before the jump.</param>
/// <param name="To">The position after the jump.</param>
/// <param name="Intent">The intent of the seek that caused the jump.</param>
/// <param name="Kind">
/// <see cref="ECutsceneDiscontinuityKind.Full"/> for a seek, a camera cut, or a stop, and
/// <see cref="ECutsceneDiscontinuityKind.ClockResync"/> for a hard resync to the audio with no camera change.
/// </param>
public readonly record struct CutsceneDiscontinuity(
    CutsceneTime From,
    CutsceneTime To,
    ESeekIntent Intent,
    ECutsceneDiscontinuityKind Kind = ECutsceneDiscontinuityKind.Full);
