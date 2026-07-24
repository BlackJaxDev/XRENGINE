using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Input;

/// <summary>
/// Lower-layer pawn contract used by controller implementations without depending on
/// the concrete PawnComponent type that still lives in a higher assembly.
/// </summary>
public interface IRuntimeInputControllablePawn : IXRNotifyPropertyChanged, IXRNotifyPropertyChanging
{
    SceneNode? SceneNode => (this as XRComponent)?.SceneNode;
    IPawnController? Controller { get; set; }
    object? Viewport { get; }
    object? RuntimeCameraComponent { get; }
    ICollection<XRComponent> LinkedUiInputComponents { get; }
    void RegisterControllerInput(object inputInterface);
    void EnqueuePossessionByLocalPlayer(ELocalPlayerIndex player);
}
