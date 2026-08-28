using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Data;

namespace XREngine.Components.Animation;

public partial class AnimationClipComponent
{
    private readonly ImportedAnimationEventBuffer _importedAnimationEventBuffer = new();
    private long _sourceEventUnwrappedPlaybackTimeTicks;
    private bool _dispatchInitialSourceEvents;

    /// <summary>Raised after an imported AnimationEvent is dispatched to scene components.</summary>
    public event Action<ImportedAnimationEventOccurrence>? ImportedAnimationEventTriggered;

    private void PrimeImportedAnimationEventClock(AnimationClip clip, long initialTicks)
    {
        _sourceEventUnwrappedPlaybackTimeTicks = initialTicks;
        _importedAnimationEventBuffer.EnsureCapacity(Math.Max(clip.ImportedEvents.Length, 4));
        _dispatchInitialSourceEvents = clip.ImportedEvents.Length > 0;
    }

    private void DispatchInitialImportedAnimationEvents()
    {
        if (!_dispatchInitialSourceEvents || Animation is null)
            return;

        _dispatchInitialSourceEvents = false;
        _importedAnimationEventBuffer.Clear();
        Animation.CollectImportedAnimationEventsAtTime(
            _importedAnimationEventBuffer,
            StopwatchTicksToSeconds(_sourceEventUnwrappedPlaybackTimeTicks));
        DispatchBufferedImportedAnimationEvents();
    }

    private void AdvanceAndDispatchImportedAnimationEvents(long deltaTicks)
    {
        if (Animation is null || Animation.ImportedEvents.Length == 0)
        {
            _sourceEventUnwrappedPlaybackTimeTicks = SaturatingAddStopwatchTicks(
                _sourceEventUnwrappedPlaybackTimeTicks,
                deltaTicks);
            return;
        }

        long previousTicks = _sourceEventUnwrappedPlaybackTimeTicks;
        long currentTicks = SaturatingAddStopwatchTicks(previousTicks, deltaTicks);
        _sourceEventUnwrappedPlaybackTimeTicks = currentTicks;
        _importedAnimationEventBuffer.Clear();
        Animation.CollectImportedAnimationEvents(
            _importedAnimationEventBuffer,
            StopwatchTicksToSeconds(previousTicks),
            StopwatchTicksToSeconds(currentTicks),
            includePrevious: false);
        DispatchBufferedImportedAnimationEvents();
    }

    private void DispatchBufferedImportedAnimationEvents()
    {
        foreach (ref readonly ImportedAnimationEventOccurrence occurrence in _importedAnimationEventBuffer.Items)
        {
            int receiverCount = ImportedAnimationEventDispatcher.Dispatch(this, occurrence);
            ImportedAnimationEventTriggered?.Invoke(occurrence);
            if (receiverCount == 0
                && occurrence.Event.MessageOptions == EImportedAnimationEventMessageOptions.RequireReceiver)
            {
                Debug.Animation(
                    $"[AnimationEvent] '{occurrence.Event.FunctionName}' from '{occurrence.Clip.Name}' " +
                    $"had no compatible receiver on '{SceneNode.Name}'.");
            }
        }
    }
}
