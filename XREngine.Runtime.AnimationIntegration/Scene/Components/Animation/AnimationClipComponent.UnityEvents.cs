using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Data;

namespace XREngine.Components.Animation;

public partial class AnimationClipComponent
{
    private readonly UnityAnimationEventBuffer _unityAnimationEventBuffer = new();
    private long _unityEventUnwrappedPlaybackTimeTicks;
    private bool _dispatchInitialUnityEvents;

    /// <summary>Raised after an imported AnimationEvent is dispatched to scene components.</summary>
    public event Action<UnityAnimationEventOccurrence>? UnityAnimationEventTriggered;

    private void PrimeUnityAnimationEventClock(AnimationClip clip, long initialTicks)
    {
        _unityEventUnwrappedPlaybackTimeTicks = initialTicks;
        _unityAnimationEventBuffer.EnsureCapacity(Math.Max(clip.UnityEvents.Length, 4));
        _dispatchInitialUnityEvents = clip.UnityEvents.Length > 0;
    }

    private void DispatchInitialUnityAnimationEvents()
    {
        if (!_dispatchInitialUnityEvents || Animation is null)
            return;

        _dispatchInitialUnityEvents = false;
        _unityAnimationEventBuffer.Clear();
        Animation.CollectUnityAnimationEventsAtTime(
            _unityAnimationEventBuffer,
            StopwatchTicksToSeconds(_unityEventUnwrappedPlaybackTimeTicks));
        DispatchBufferedUnityAnimationEvents();
    }

    private void AdvanceAndDispatchUnityAnimationEvents(long deltaTicks)
    {
        if (Animation is null || Animation.UnityEvents.Length == 0)
        {
            _unityEventUnwrappedPlaybackTimeTicks = SaturatingAddStopwatchTicks(
                _unityEventUnwrappedPlaybackTimeTicks,
                deltaTicks);
            return;
        }

        long previousTicks = _unityEventUnwrappedPlaybackTimeTicks;
        long currentTicks = SaturatingAddStopwatchTicks(previousTicks, deltaTicks);
        _unityEventUnwrappedPlaybackTimeTicks = currentTicks;
        _unityAnimationEventBuffer.Clear();
        Animation.CollectUnityAnimationEvents(
            _unityAnimationEventBuffer,
            StopwatchTicksToSeconds(previousTicks),
            StopwatchTicksToSeconds(currentTicks),
            includePrevious: false);
        DispatchBufferedUnityAnimationEvents();
    }

    private void DispatchBufferedUnityAnimationEvents()
    {
        foreach (ref readonly UnityAnimationEventOccurrence occurrence in _unityAnimationEventBuffer.Items)
        {
            int receiverCount = UnityAnimationEventDispatcher.Dispatch(this, occurrence);
            UnityAnimationEventTriggered?.Invoke(occurrence);
            if (receiverCount == 0
                && occurrence.Event.MessageOptions == EUnityAnimationEventMessageOptions.RequireReceiver)
            {
                Debug.Animation(
                    $"[AnimationEvent] '{occurrence.Event.FunctionName}' from '{occurrence.Clip.Name}' " +
                    $"had no compatible receiver on '{SceneNode.Name}'.");
            }
        }
    }
}
