using XREngine.Components;
using XREngine.Input;
using XREngine.Input.Devices;

namespace RenderingParity;

/// <summary>Provides small inspection controls for the authored camera and animation.</summary>
public sealed class RenderingParityPawnComponent : PawnComponent
{
    private RenderingParityAnimationComponent? _animation;

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        CameraComponent = SceneNode.GetComponent<CameraComponent>()
            ?? throw new InvalidOperationException("The authored inspection pawn requires its camera.");
        _animation = SceneNode.Parent?.GetComponent<RenderingParityAnimationComponent>()
            ?? throw new InvalidOperationException("The authored inspection pawn requires its animation root.");
    }

    public override void RegisterInput(object inputInterface)
    {
        if (inputInterface is not InputInterface input)
            return;
        input.RegisterKeyEvent(EKey.Space, EButtonInputType.Pressed, ToggleAnimation);
        input.RegisterKeyEvent(EKey.R, EButtonInputType.Pressed, ResetAnimation);
    }

    private void ToggleAnimation()
    {
        if (_animation is not null)
            _animation.AnimationPaused = !_animation.AnimationPaused;
    }

    private void ResetAnimation() => _animation?.ResetAnimation();
}
