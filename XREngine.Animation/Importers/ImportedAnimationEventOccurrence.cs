namespace XREngine.Animation.Importers;

/// <summary>One ordered crossing of an AnimationEvent during playback.</summary>
public readonly record struct ImportedAnimationEventOccurrence(
    AnimationClip Clip,
    ImportedAnimationEvent Event,
    long LoopCycle,
    bool Reverse,
    ulong MotionOccurrenceId = 0,
    string StateName = "",
    float BlendWeight = 1.0f);
