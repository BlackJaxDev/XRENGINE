using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    internal void ResetUnityHumanoidStateClockFromUnwrappedTicks(long playbackTicks)
    {
        long lengthTicks = SecondsToUnityHumanoidTicks(LengthInSeconds);
        bool loop = UnityHumanoidRootMotionSettings is { } settings
            && UnityHumanoidRootMotionPolicy.TryCreate(
                settings,
                out UnityHumanoidRootMotionPolicy policy,
                out _)
            && policy.LoopTime;
        if (loop && lengthTicks > 0L)
        {
            _unityHumanoidStateLoopCycle = FloorDivide(playbackTicks, lengthTicks);
            _unityHumanoidStatePlaybackTicks = FloorModulus(playbackTicks, lengthTicks);
        }
        else
        {
            _unityHumanoidStateLoopCycle = 0L;
            _unityHumanoidStatePlaybackTicks = Math.Clamp(
                playbackTicks,
                0L,
                Math.Max(0L, lengthTicks));
        }

        _unityHumanoidStateClockInitialized = true;
        _unityHumanoidSourceWrapped = false;
    }

    internal bool TryCreateUnityHumanoidMotionContribution(
        double normalizedTime,
        float weight,
        ulong occurrenceId,
        ulong lifecycleGeneration,
        bool inheritedMirror,
        out UnityHumanoidMotionContribution contribution)
    {
        contribution = default;
        if (!HasRootMotion
            || UnityHumanoidRootMotionSettings is not { } settings
            || !UnityHumanoidRootMotionPolicy.TryCreate(
                settings,
                out UnityHumanoidRootMotionPolicy policy,
                out _))
            return false;

        if (inheritedMirror)
            policy = policy with { Mirror = !policy.Mirror };

        long lengthTicks = SecondsToUnityHumanoidTicks(LengthInSeconds);
        long playbackTicks = NormalizedTimeToUnityHumanoidTicks(normalizedTime, lengthTicks);
        long phaseTicks = 0L;
        long playbackLoopCycle = 0L;
        long sourceLoopCycle = 0L;
        if (lengthTicks > 0L)
        {
            if (policy.LoopTime)
            {
                playbackLoopCycle = FloorDivide(playbackTicks, lengthTicks);
                phaseTicks = FloorModulus(playbackTicks, lengthTicks);
                long offsetTicks = SecondsToUnityHumanoidTicks(
                    policy.NormalizedCycleOffset * LengthInSeconds);
                long shiftedTicks = SaturatingAdd(phaseTicks, offsetTicks);
                sourceLoopCycle = SaturatingAdd(
                    playbackLoopCycle,
                    FloorDivide(shiftedTicks, lengthTicks));
                phaseTicks = FloorModulus(shiftedTicks, lengthTicks);
            }
            else
            {
                long offsetTicks = SecondsToUnityHumanoidTicks(
                    policy.NormalizedCycleOffset * LengthInSeconds);
                phaseTicks = Math.Clamp(
                    SaturatingAdd(playbackTicks, offsetTicks),
                    0L,
                    lengthTicks);
            }
        }

        float sampleTime = UnityHumanoidTicksToSeconds(phaseTicks);
        float samplePhase = lengthTicks > 0L
            ? Math.Clamp((float)(phaseTicks / (double)lengthTicks), 0.0f, 1.0f)
            : 0.0f;
        float safeWeight = float.IsFinite(weight) ? Math.Max(0.0f, weight) : 0.0f;
        contribution = new UnityHumanoidMotionContribution(
            this,
            policy,
            occurrenceId,
            lifecycleGeneration,
            safeWeight,
            sampleTime,
            samplePhase,
            playbackLoopCycle,
            sourceLoopCycle,
            EUnityHumanoidMotionContributionType.Override);
        return true;
    }

    internal void SeekClipPlaybackFromNormalizedStateTime(double normalizedTime)
    {
        long lengthTicks = SecondsToUnityHumanoidTicks(LengthInSeconds);
        long playbackTicks = NormalizedTimeToUnityHumanoidTicks(normalizedTime, lengthTicks);
        ResetUnityHumanoidStateClockFromUnwrappedTicks(playbackTicks);
        float timeSeconds = (float)ResolveUnityPlaybackTime(
            UnityHumanoidTicksToSeconds(playbackTicks),
            out _,
            out _);
        foreach (AnimationMember member in _animatedCurves.Values)
            member.Animation?.Seek(timeSeconds, wrapLooped: false);
    }

    private static long NormalizedTimeToUnityHumanoidTicks(double normalizedTime, long lengthTicks)
    {
        if (!double.IsFinite(normalizedTime) || lengthTicks <= 0L || normalizedTime == 0.0)
            return 0L;

        double ticks = normalizedTime * lengthTicks;
        if (ticks >= long.MaxValue)
            return long.MaxValue;
        if (ticks <= long.MinValue)
            return long.MinValue;
        return (long)Math.Round(ticks);
    }

    private static long FloorDivide(long value, long divisor)
    {
        long quotient = value / divisor;
        if (value % divisor < 0L)
            quotient--;
        return quotient;
    }

    private static long FloorModulus(long value, long divisor)
    {
        long remainder = value % divisor;
        return remainder < 0L ? remainder + divisor : remainder;
    }

    private static long SaturatingAdd(long first, long second)
    {
        if (second > 0L && first > long.MaxValue - second)
            return long.MaxValue;
        if (second < 0L && first < long.MinValue - second)
            return long.MinValue;
        return first + second;
    }
}
