using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private UnityAnimationClipMetadata? _unityMetadata;

    /// <summary>
    /// Unity clip header metadata retained by the native import and playback
    /// path, including wrap behavior and authored culling bounds.
    /// </summary>
    [MemoryPackIgnore]
    public UnityAnimationClipMetadata? UnityMetadata
    {
        get => _unityMetadata;
        set => SetField(ref _unityMetadata, value);
    }

    /// <summary>
    /// Resolves the serialized playback mode. Unity's legacy wrap mode is
    /// authoritative only for legacy clips; Mecanim clips use Loop Time.
    /// </summary>
    public EUnityAnimationWrapMode EffectiveUnityWrapMode
        => UnityMetadata is { Legacy: true, WrapMode: not EUnityAnimationWrapMode.Default } metadata
            ? metadata.WrapMode
            : Looped
                ? EUnityAnimationWrapMode.Loop
                : EUnityAnimationWrapMode.Once;

    public bool UsesCyclicUnityPlayback
        => EffectiveUnityWrapMode is EUnityAnimationWrapMode.Loop or EUnityAnimationWrapMode.PingPong;

    /// <summary>
    /// Maps an unbounded playback clock to the authored clip interval. The
    /// returned cycle identifies the loop or ping-pong leg containing the time.
    /// </summary>
    public double ResolveUnityPlaybackTime(
        double unwrappedSeconds,
        out long cycle,
        out bool reverse)
    {
        cycle = 0;
        reverse = false;
        double duration = LengthInSeconds;
        if (!double.IsFinite(unwrappedSeconds) || !(duration > 0.0))
            return 0.0;

        switch (EffectiveUnityWrapMode)
        {
            case EUnityAnimationWrapMode.Loop:
                cycle = FloorUnityCycle(unwrappedSeconds / duration);
                return unwrappedSeconds - cycle * duration;
            case EUnityAnimationWrapMode.PingPong:
                cycle = FloorUnityCycle(unwrappedSeconds / duration);
                double legTime = unwrappedSeconds - cycle * duration;
                reverse = (cycle & 1L) != 0L;
                return reverse ? duration - legTime : legTime;
            default:
                return Math.Clamp(unwrappedSeconds, 0.0, duration);
        }
    }

    private static long FloorUnityCycle(double value)
    {
        if (value <= long.MinValue)
            return long.MinValue;
        if (value >= long.MaxValue)
            return long.MaxValue;
        return (long)Math.Floor(value);
    }
}
