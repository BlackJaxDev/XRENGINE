namespace XREngine.Animation.Importers;

/// <summary>
/// Typed alternative to Unity's name-based AnimationEvent dispatch. Runtime
/// hosts may additionally support compatible named component methods.
/// </summary>
public interface IImportedAnimationEventReceiver
{
    void ReceiveImportedAnimationEvent(in ImportedAnimationEventOccurrence occurrence);
}
