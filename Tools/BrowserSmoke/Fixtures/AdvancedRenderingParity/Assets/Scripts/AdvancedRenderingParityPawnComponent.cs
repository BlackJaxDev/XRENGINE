using XREngine.Components;

namespace AdvancedRenderingParity;

/// <summary>Owns the fixed authored inspection camera.</summary>
public sealed class AdvancedRenderingParityPawnComponent : PawnComponent
{
    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        CameraComponent = SceneNode.GetComponent<CameraComponent>()
            ?? throw new InvalidOperationException("The authored inspection pawn requires its camera.");
    }
}
