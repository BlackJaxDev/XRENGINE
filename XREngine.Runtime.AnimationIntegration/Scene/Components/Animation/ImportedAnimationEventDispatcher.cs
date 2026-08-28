using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>
/// Delivers imported animation events to explicitly typed receivers on the animated node.
/// </summary>
/// <remarks>
/// Only events mapped by <see cref="ImportedAnimationEventAllowlist"/> reach this dispatcher.
/// Native event identifiers are never interpreted as component method names. Components opt in
/// by implementing <see cref="IImportedAnimationEventReceiver"/>.
/// </remarks>
internal static class ImportedAnimationEventDispatcher
{
    public static int Dispatch(XRComponent owner, in ImportedAnimationEventOccurrence occurrence)
    {
        int receiverCount = 0;
        foreach (XRComponent component in owner.SceneNode.Components)
        {
            if (component is not IImportedAnimationEventReceiver typedReceiver)
                continue;

            typedReceiver.ReceiveImportedAnimationEvent(occurrence);
            receiverCount++;
        }

        return receiverCount;
    }
}
