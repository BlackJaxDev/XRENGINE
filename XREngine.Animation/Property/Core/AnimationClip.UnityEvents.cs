using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private UnityAnimationEvent[] _unityEvents = [];

    /// <summary>Animation events sorted by authored time and stable source order.</summary>
    [MemoryPackIgnore]
    public UnityAnimationEvent[] UnityEvents
    {
        get => _unityEvents;
        set => SetField(ref _unityEvents, value ?? []);
    }

    public void CollectUnityAnimationEvents(
        UnityAnimationEventBuffer destination,
        double previousSeconds,
        double currentSeconds,
        bool includePrevious,
        ulong occurrenceId = 0,
        float blendWeight = 1.0f)
    {
        if (_unityEvents.Length == 0
            || !double.IsFinite(previousSeconds)
            || !double.IsFinite(currentSeconds)
            || previousSeconds == currentSeconds
            || !(LengthInSeconds > 0.0f))
            return;

        bool reverse = currentSeconds < previousSeconds;
        EUnityAnimationWrapMode wrapMode = EffectiveUnityWrapMode;
        if (wrapMode == EUnityAnimationWrapMode.PingPong)
        {
            CollectPingPongUnityAnimationEvents(
                destination,
                previousSeconds,
                currentSeconds,
                includePrevious,
                occurrenceId,
                blendWeight);
            return;
        }

        if (wrapMode != EUnityAnimationWrapMode.Loop)
        {
            double previousLocal = Math.Clamp(previousSeconds, 0.0, LengthInSeconds);
            double currentLocal = Math.Clamp(currentSeconds, 0.0, LengthInSeconds);
            if (previousLocal == currentLocal)
                return;

            CollectUnityAnimationEventsInCycle(
                destination,
                previousLocal,
                currentLocal,
                cycle: 0,
                reverse,
                includePrevious,
                occurrenceId,
                blendWeight);
            return;
        }

        double duration = LengthInSeconds;
        long firstCycle = FloorToLong((reverse ? currentSeconds : previousSeconds) / duration);
        long lastCycle = FloorToLong((reverse ? previousSeconds : currentSeconds) / duration);
        if (!reverse)
        {
            for (long cycle = firstCycle; cycle <= lastCycle; cycle++)
            {
                double cycleStart = cycle * duration;
                CollectUnityAnimationEventsInCycle(
                    destination,
                    previousSeconds - cycleStart,
                    currentSeconds - cycleStart,
                    cycle,
                    reverse: false,
                    includePrevious,
                    occurrenceId,
                    blendWeight);
                if (cycle == long.MaxValue)
                    break;
            }
            return;
        }

        for (long cycle = lastCycle; cycle >= firstCycle; cycle--)
        {
            double cycleStart = cycle * duration;
            CollectUnityAnimationEventsInCycle(
                destination,
                previousSeconds - cycleStart,
                currentSeconds - cycleStart,
                cycle,
                reverse: true,
                includePrevious,
                occurrenceId,
                blendWeight);
            if (cycle == long.MinValue)
                break;
        }
    }

    public void CollectUnityAnimationEventsAtTime(
        UnityAnimationEventBuffer destination,
        double seconds,
        ulong occurrenceId = 0,
        float blendWeight = 1.0f)
    {
        if (_unityEvents.Length == 0 || !(LengthInSeconds > 0.0f) || !double.IsFinite(seconds))
            return;

        double local = ResolveUnityPlaybackTime(seconds, out long cycle, out bool reverse);
        for (int i = 0; i < _unityEvents.Length; i++)
        {
            UnityAnimationEvent animationEvent = _unityEvents[i];
            if (Math.Abs(animationEvent.Time - local) > 0.000001)
                continue;
            destination.Add(new UnityAnimationEventOccurrence(this, animationEvent, cycle, reverse, occurrenceId, BlendWeight: blendWeight));
        }
    }

    private void CollectUnityAnimationEventsInCycle(
        UnityAnimationEventBuffer destination,
        double previousLocal,
        double currentLocal,
        long cycle,
        bool reverse,
        bool includePrevious,
        ulong occurrenceId,
        float blendWeight)
    {
        if (!reverse)
        {
            for (int i = 0; i < _unityEvents.Length; i++)
            {
                UnityAnimationEvent animationEvent = _unityEvents[i];
                bool afterPrevious = includePrevious
                    ? animationEvent.Time >= previousLocal
                    : animationEvent.Time > previousLocal;
                if (afterPrevious && animationEvent.Time <= currentLocal)
                    destination.Add(new UnityAnimationEventOccurrence(this, animationEvent, cycle, false, occurrenceId, BlendWeight: blendWeight));
            }
            return;
        }

        for (int groupEnd = _unityEvents.Length - 1; groupEnd >= 0;)
        {
            float groupTime = _unityEvents[groupEnd].Time;
            int groupStart = groupEnd;
            while (groupStart > 0 && _unityEvents[groupStart - 1].Time == groupTime)
                groupStart--;

            UnityAnimationEvent animationEvent = _unityEvents[groupStart];
            bool beforePrevious = includePrevious
                ? animationEvent.Time <= previousLocal
                : animationEvent.Time < previousLocal;
            if (beforePrevious && animationEvent.Time >= currentLocal)
            {
                for (int i = groupStart; i <= groupEnd; i++)
                {
                    destination.Add(new UnityAnimationEventOccurrence(
                        this,
                        _unityEvents[i],
                        cycle,
                        true,
                        occurrenceId,
                        BlendWeight: blendWeight));
                }
            }
            groupEnd = groupStart - 1;
        }
    }

    private void CollectPingPongUnityAnimationEvents(
        UnityAnimationEventBuffer destination,
        double previousSeconds,
        double currentSeconds,
        bool includePrevious,
        ulong occurrenceId,
        float blendWeight)
    {
        double duration = LengthInSeconds;
        bool clockReverse = currentSeconds < previousSeconds;
        long firstLeg = FloorToLong((clockReverse ? currentSeconds : previousSeconds) / duration);
        long lastLeg = FloorToLong((clockReverse ? previousSeconds : currentSeconds) / duration);
        bool firstSegment = true;

        if (!clockReverse)
        {
            for (long leg = firstLeg; leg <= lastLeg; leg++)
            {
                double legStart = leg * duration;
                double segmentStart = Math.Max(previousSeconds, legStart);
                double segmentEnd = Math.Min(currentSeconds, legStart + duration);
                if (segmentEnd > segmentStart)
                {
                    bool reflected = (leg & 1L) != 0L;
                    double previousLocal = reflected
                        ? duration - (segmentStart - legStart)
                        : segmentStart - legStart;
                    double currentLocal = reflected
                        ? duration - (segmentEnd - legStart)
                        : segmentEnd - legStart;
                    CollectUnityAnimationEventsInCycle(
                        destination,
                        previousLocal,
                        currentLocal,
                        leg,
                        reverse: reflected,
                        includePrevious && firstSegment,
                        occurrenceId,
                        blendWeight);
                    firstSegment = false;
                }
                if (leg == long.MaxValue)
                    break;
            }
            return;
        }

        for (long leg = lastLeg; leg >= firstLeg; leg--)
        {
            double legStart = leg * duration;
            double segmentStart = Math.Min(previousSeconds, legStart + duration);
            double segmentEnd = Math.Max(currentSeconds, legStart);
            if (segmentStart > segmentEnd)
            {
                bool reflected = (leg & 1L) != 0L;
                double previousLocal = reflected
                    ? duration - (segmentStart - legStart)
                    : segmentStart - legStart;
                double currentLocal = reflected
                    ? duration - (segmentEnd - legStart)
                    : segmentEnd - legStart;
                CollectUnityAnimationEventsInCycle(
                    destination,
                    previousLocal,
                    currentLocal,
                    leg,
                    reverse: previousLocal > currentLocal,
                    includePrevious && firstSegment,
                    occurrenceId,
                    blendWeight);
                firstSegment = false;
            }
            if (leg == long.MinValue)
                break;
        }
    }

    private static long FloorToLong(double value)
    {
        if (value <= long.MinValue)
            return long.MinValue;
        if (value >= long.MaxValue)
            return long.MaxValue;
        return (long)Math.Floor(value);
    }
}
