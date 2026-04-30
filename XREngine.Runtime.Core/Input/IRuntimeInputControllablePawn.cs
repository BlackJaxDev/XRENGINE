using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Input;

/// <summary>
/// Lower-layer pawn contract used by controller implementations without depending on
/// the concrete PawnComponent type that still lives in a higher assembly.
/// </summary>
public interface IRuntimeInputControllablePawn
{
    SceneNode? SceneNode => (this as XRComponent)?.SceneNode;
    IPawnController? Controller { get; set; }
    object? RuntimeCameraComponent { get; }
    void RegisterControllerInput(object inputInterface);
    void EnqueuePossessionByLocalPlayer(ELocalPlayerIndex player);
}