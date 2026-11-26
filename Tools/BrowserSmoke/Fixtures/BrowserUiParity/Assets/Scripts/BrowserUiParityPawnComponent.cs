using XREngine.Components;

namespace BrowserUiParity;

/// <summary>Keeps the authored camera fixed while its linked canvases receive shared player input.</summary>
public sealed class BrowserUiParityPawnComponent : PawnComponent
{
    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        if (CameraComponent is not CameraComponent camera || !ReferenceEquals(camera.SceneNode, SceneNode))
            throw new InvalidOperationException("The saved UI pawn must retain its sibling camera reference.");
    }

    public override void RegisterInput(object inputInterface) { }
}
