using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private ImportedAnimationClipMetadata? _importedMetadata;

    /// <summary>
    /// Unity clip header metadata retained by the native import and playback
    /// path, including wrap behavior and authored culling bounds.
    /// </summary>
    [MemoryPackIgnore]
    public ImportedAnimationClipMetadata? ImportedMetadata
    {
        get => _importedMetadata;
        set => SetField(ref _importedMetadata, value);
    }

    /// <summary>
    /// Resolves the serialized playback mode. Unity's legacy wrap mode is
    /// authoritative only for legacy clips; Mecanim clips use Loop Time.
    /// </summary>
    public EImportedAnimationWrapMode EffectiveSourceWrapMode
        => ImportedMetadata is { Legacy: true, WrapMode: not EImportedAnimationWrapMode.Default } metadata
            ? metadata.WrapMode
            : Looped
                ? EImportedAnimationWrapMode.Loop
                : EImportedAnimationWrapMode.Once;

    public bool UsesCyclicSourcePlayback
        => EffectiveSourceWrapMode is EImportedAnimationWrapMode.Loop or EImportedAnimationWrapMode.PingPong;

    /// <summary>
    /// Maps an unbounded playback clock to the authored clip interval. The
    /// returned cycle identifies the loop or ping-pong leg containing the time.
    /// </summary>
    public double ResolveSourcePlaybackTime(
        double unwrappedSeconds,
        out long cycle,
        out bool reverse)
    {
        cycle = 0;
        reverse = false;
        double duration = LengthInSeconds;
        if (!double.IsFinite(unwrappedSeconds) || !(duration > 0.0))
            return 0.0;

        switch (EffectiveSourceWrapMode)
        {
            case EImportedAnimationWrapMode.Loop:
                cycle = FloorSourceCycle(unwrappedSeconds / duration);
                return unwrappedSeconds - cycle * duration;
            case EImportedAnimationWrapMode.PingPong:
                cycle = FloorSourceCycle(unwrappedSeconds / duration);
                double legTime = unwrappedSeconds - cycle * duration;
                reverse = (cycle & 1L) != 0L;
                return reverse ? duration - legTime : legTime;
            default:
                return Math.Clamp(unwrappedSeconds, 0.0, duration);
        }
    }

    private static long FloorSourceCycle(double value)
    {
        if (value <= long.MinValue)
            return long.MinValue;
        if (value >= long.MaxValue)
            return long.MaxValue;
        return (long)Math.Floor(value);
    }
}
