using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    internal void ResetImportedHumanoidStateClockFromUnwrappedTicks(long playbackTicks)
    {
        long lengthTicks = SecondsToImportedHumanoidTicks(LengthInSeconds);
        bool loop = ImportedHumanoidRootMotionSettings is { } settings
            && ImportedHumanoidRootMotionPolicy.TryCreate(
                settings,
                out ImportedHumanoidRootMotionPolicy policy,
                out _)
            && policy.LoopTime;
        if (loop && lengthTicks > 0L)
        {
            _importedHumanoidStateLoopCycle = FloorDivide(playbackTicks, lengthTicks);
            _importedHumanoidStatePlaybackTicks = FloorModulus(playbackTicks, lengthTicks);
        }
        else
        {
            _importedHumanoidStateLoopCycle = 0L;
            _importedHumanoidStatePlaybackTicks = Math.Clamp(
                playbackTicks,
                0L,
                Math.Max(0L, lengthTicks));
        }

        _importedHumanoidStateClockInitialized = true;
        _importedHumanoidSourceWrapped = false;
    }

    internal bool TryCreateImportedHumanoidMotionContribution(
        double normalizedTime,
        float weight,
        ulong occurrenceId,
        ulong lifecycleGeneration,
        bool inheritedMirror,
        out HumanoidMotionContribution contribution)
    {
        contribution = default;
        if (!HasRootMotion
            || ImportedHumanoidRootMotionSettings is not { } settings
            || !ImportedHumanoidRootMotionPolicy.TryCreate(
                settings,
                out ImportedHumanoidRootMotionPolicy policy,
                out _))
            return false;

        if (inheritedMirror)
            policy = policy with { Mirror = !policy.Mirror };

        long lengthTicks = SecondsToImportedHumanoidTicks(LengthInSeconds);
        long playbackTicks = NormalizedTimeToImportedHumanoidTicks(normalizedTime, lengthTicks);
        long phaseTicks = 0L;
        long playbackLoopCycle = 0L;
        long sourceLoopCycle = 0L;
        if (lengthTicks > 0L)
        {
            if (policy.LoopTime)
            {
                playbackLoopCycle = FloorDivide(playbackTicks, lengthTicks);
                phaseTicks = FloorModulus(playbackTicks, lengthTicks);
                long offsetTicks = SecondsToImportedHumanoidTicks(
                    policy.NormalizedCycleOffset * LengthInSeconds);
                long shiftedTicks = SaturatingAdd(phaseTicks, offsetTicks);
                sourceLoopCycle = SaturatingAdd(
                    playbackLoopCycle,
                    FloorDivide(shiftedTicks, lengthTicks));
                phaseTicks = FloorModulus(shiftedTicks, lengthTicks);
            }
            else
            {
                long offsetTicks = SecondsToImportedHumanoidTicks(
                    policy.NormalizedCycleOffset * LengthInSeconds);
                phaseTicks = Math.Clamp(
                    SaturatingAdd(playbackTicks, offsetTicks),
                    0L,
                    lengthTicks);
            }
        }

        float sampleTime = ImportedHumanoidTicksToSeconds(phaseTicks);
        float samplePhase = lengthTicks > 0L
            ? Math.Clamp((float)(phaseTicks / (double)lengthTicks), 0.0f, 1.0f)
            : 0.0f;
        float safeWeight = float.IsFinite(weight) ? Math.Max(0.0f, weight) : 0.0f;
        contribution = new HumanoidMotionContribution(
            this,
            policy,
            occurrenceId,
            lifecycleGeneration,
            safeWeight,
            sampleTime,
            samplePhase,
            playbackLoopCycle,
            sourceLoopCycle,
            EHumanoidMotionContributionType.Override);
        return true;
    }

    internal void SeekClipPlaybackFromNormalizedStateTime(double normalizedTime)
    {
        long lengthTicks = SecondsToImportedHumanoidTicks(LengthInSeconds);
        long playbackTicks = NormalizedTimeToImportedHumanoidTicks(normalizedTime, lengthTicks);
        ResetImportedHumanoidStateClockFromUnwrappedTicks(playbackTicks);
        float timeSeconds = (float)ResolveSourcePlaybackTime(
            ImportedHumanoidTicksToSeconds(playbackTicks),
            out _,
            out _);
        foreach (AnimationMember member in _animatedCurves.Values)
            member.Animation?.Seek(timeSeconds, wrapLooped: false);
    }

    internal void SeekClipPlaybackFromSourceSeconds(float sourceTimeSeconds)
    {
        float safeTime = float.IsFinite(sourceTimeSeconds)
            ? Math.Clamp(sourceTimeSeconds, 0.0f, Math.Max(0.0f, LengthInSeconds))
            : 0.0f;
        long playbackTicks = SecondsToImportedHumanoidTicks(safeTime);
        ResetImportedHumanoidStateClockFromUnwrappedTicks(playbackTicks);
        foreach (AnimationMember member in _animatedCurves.Values)
            member.Animation?.Seek(safeTime, wrapLooped: false);
    }

    private static long NormalizedTimeToImportedHumanoidTicks(double normalizedTime, long lengthTicks)
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
