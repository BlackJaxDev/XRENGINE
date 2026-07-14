using System.Threading;

namespace XREngine.Rendering;

/// <summary>
/// Deterministic rendered-frame scenarios used by the Phase 5.2.4b temporal
/// acceptance workload. The definitions are fixed before a run so live
/// captures cannot change their oracle after observing output.
/// </summary>
public enum EPhase524bTemporalScenario
{
    ObjectMotion,
    StaticPose,
    HeadRotation,
    HeadTranslation,
    Disocclusion,
    MotionStop,
}

public enum EPhase524bTemporalSample
{
    ObjectMotionActive,
    StaticPoseSettled,
    HeadRotationActive,
    HeadTranslationActive,
    DisocclusionOccluded,
    DisocclusionRevealed,
    MotionStopMoving,
    MotionStopSettled,
}

public enum EPhase524bVelocityOracle
{
    Zero,
    PositiveX,
    NegativeX,
}

public readonly record struct Phase524bTemporalSampleDefinition(
    EPhase524bTemporalSample Sample,
    EPhase524bTemporalScenario Scenario,
    EPhase524bVelocityOracle VelocityOracle,
    int CaptureStartFrame,
    int CaptureEndFrame,
    bool RequiresTemporalConvergence,
    bool IsDisocclusionBaseline,
    bool IsDisocclusionResult);

/// <summary>
/// Allocation-free bridge between the validation scene driver and diagnostic
/// render commands. Sequence time advances only after a strict-SPS TSR output
/// has completed, so startup/update timing cannot skip an evidence window.
/// </summary>
public static class Phase524bTemporalScenarioDiagnostics
{
    public const int SequenceCompleteFrame = 72;
    public const int PostSequenceMotionPeriodFrames = 120;
    public const int BoundaryCaptureMotionTickStep = 30;

    private static readonly Phase524bTemporalSampleDefinition[] s_definitions =
    [
        new(
            EPhase524bTemporalSample.ObjectMotionActive,
            EPhase524bTemporalScenario.ObjectMotion,
            EPhase524bVelocityOracle.PositiveX,
            8,
            10,
            RequiresTemporalConvergence: false,
            IsDisocclusionBaseline: false,
            IsDisocclusionResult: false),
        new(
            EPhase524bTemporalSample.StaticPoseSettled,
            EPhase524bTemporalScenario.StaticPose,
            EPhase524bVelocityOracle.Zero,
            16,
            18,
            RequiresTemporalConvergence: true,
            IsDisocclusionBaseline: false,
            IsDisocclusionResult: false),
        new(
            EPhase524bTemporalSample.HeadRotationActive,
            EPhase524bTemporalScenario.HeadRotation,
            EPhase524bVelocityOracle.PositiveX,
            24,
            26,
            RequiresTemporalConvergence: false,
            IsDisocclusionBaseline: false,
            IsDisocclusionResult: false),
        new(
            EPhase524bTemporalSample.HeadTranslationActive,
            EPhase524bTemporalScenario.HeadTranslation,
            EPhase524bVelocityOracle.NegativeX,
            32,
            34,
            RequiresTemporalConvergence: false,
            IsDisocclusionBaseline: false,
            IsDisocclusionResult: false),
        new(
            EPhase524bTemporalSample.DisocclusionOccluded,
            EPhase524bTemporalScenario.Disocclusion,
            EPhase524bVelocityOracle.Zero,
            38,
            40,
            RequiresTemporalConvergence: false,
            IsDisocclusionBaseline: true,
            IsDisocclusionResult: false),
        new(
            EPhase524bTemporalSample.DisocclusionRevealed,
            EPhase524bTemporalScenario.Disocclusion,
            EPhase524bVelocityOracle.Zero,
            50,
            51,
            RequiresTemporalConvergence: true,
            IsDisocclusionBaseline: false,
            IsDisocclusionResult: true),
        new(
            EPhase524bTemporalSample.MotionStopMoving,
            EPhase524bTemporalScenario.MotionStop,
            EPhase524bVelocityOracle.PositiveX,
            56,
            58,
            RequiresTemporalConvergence: false,
            IsDisocclusionBaseline: false,
            IsDisocclusionResult: false),
        new(
            EPhase524bTemporalSample.MotionStopSettled,
            EPhase524bTemporalScenario.MotionStop,
            EPhase524bVelocityOracle.Zero,
            68,
            70,
            RequiresTemporalConvergence: true,
            IsDisocclusionBaseline: false,
            IsDisocclusionResult: false),
    ];

    private static int s_sequenceFrame;
    private static long s_lastCompletedRenderFrameId = -1L;
    private static int s_boundaryCaptureMotionTick = -1;

    public static int SequenceFrame => Volatile.Read(ref s_sequenceFrame);

    public static ReadOnlySpan<Phase524bTemporalSampleDefinition> Definitions => s_definitions;

    public static void Reset()
    {
        Volatile.Write(ref s_sequenceFrame, 0);
        Volatile.Write(ref s_lastCompletedRenderFrameId, -1L);
        ClearBoundaryCaptureMotion();
        Phase524bCaptureReadinessDiagnostics.Reset();
    }

    public static bool TryGetBoundaryCaptureMotionTick(out uint tick)
    {
        int configuredTick = Volatile.Read(ref s_boundaryCaptureMotionTick);
        if (configuredTick < 0)
        {
            tick = 0u;
            return false;
        }

        tick = checked((uint)configuredTick);
        return true;
    }

    internal static void SetBoundaryCaptureMotionIndex(int motionIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(motionIndex);
        Volatile.Write(
            ref s_boundaryCaptureMotionTick,
            checked(motionIndex * BoundaryCaptureMotionTickStep));
    }

    internal static void ClearBoundaryCaptureMotion()
        => Volatile.Write(ref s_boundaryCaptureMotionTick, -1);

    public static bool TryGetActiveCaptureSample(
        out int sampleIndex,
        out Phase524bTemporalSampleDefinition definition)
    {
        int frame = SequenceFrame;
        for (int i = 0; i < s_definitions.Length; i++)
        {
            ref readonly Phase524bTemporalSampleDefinition candidate = ref s_definitions[i];
            if (frame < candidate.CaptureStartFrame || frame > candidate.CaptureEndFrame)
                continue;

            sampleIndex = i;
            definition = candidate;
            return true;
        }

        sampleIndex = -1;
        definition = default;
        return false;
    }

    public static void CompleteStrictSpsFrame(ulong renderFrameId)
    {
        long frameId = unchecked((long)renderFrameId);
        long previous = Volatile.Read(ref s_lastCompletedRenderFrameId);
        while (previous != frameId)
        {
            long observed = Interlocked.CompareExchange(
                ref s_lastCompletedRenderFrameId,
                frameId,
                previous);
            if (observed == previous)
            {
                int currentSequenceFrame;
                int nextSequenceFrame;
                do
                {
                    currentSequenceFrame = Volatile.Read(ref s_sequenceFrame);
                    nextSequenceFrame = currentSequenceFrame < SequenceCompleteFrame
                        ? currentSequenceFrame + 1
                        : SequenceCompleteFrame +
                            ((currentSequenceFrame - SequenceCompleteFrame + 1) % PostSequenceMotionPeriodFrames);
                }
                while (Interlocked.CompareExchange(
                    ref s_sequenceFrame,
                    nextSequenceFrame,
                    currentSequenceFrame) != currentSequenceFrame);
                return;
            }

            previous = observed;
        }
    }
}
