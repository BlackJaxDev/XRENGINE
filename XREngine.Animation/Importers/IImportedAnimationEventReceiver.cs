namespace XREngine.Animation.Importers;

/// <summary>
/// Receives imported animation events through an explicit, strongly typed runtime contract.
/// Implementations dispatch on the allowlisted native <see cref="ImportedAnimationEvent.EventId"/>;
/// source callback names never reach runtime assets.
/// </summary>
public interface IImportedAnimationEventReceiver
{
    void ReceiveImportedAnimationEvent(in ImportedAnimationEventOccurrence occurrence);
}
