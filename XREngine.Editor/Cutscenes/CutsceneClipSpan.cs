using XREngine.Cutscenes;

namespace XREngine.Editor.Cutscenes;

/// <summary>The part of a clip that a move or a trim changes. An undo step stores one value before and one after the change.</summary>
/// <param name="Start">The start time of the clip.</param>
/// <param name="Duration">The length of the clip.</param>
/// <param name="ClipOffset">The time inside the clip data at which the clip starts.</param>
public readonly record struct CutsceneClipSpan(CutsceneTime Start, CutsceneTime Duration, CutsceneTime ClipOffset)
{
    /// <summary>Reads the span of a clip.</summary>
    /// <param name="clip">The clip to read.</param>
    /// <returns>The span.</returns>
    public static CutsceneClipSpan Capture(CutsceneClip clip)
        => new(clip.Start, clip.Duration, clip.ClipOffset);

    /// <summary>Writes this span to a clip.</summary>
    /// <param name="clip">The clip to change.</param>
    public void Apply(CutsceneClip clip)
    {
        clip.Start = Start;
        clip.Duration = Duration;
        clip.ClipOffset = ClipOffset;
    }
}
