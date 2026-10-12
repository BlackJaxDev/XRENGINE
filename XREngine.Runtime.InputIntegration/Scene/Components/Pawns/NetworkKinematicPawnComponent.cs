using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Captures keyboard movement for the managed server's plain-transform character simulation.
/// The networking manager owns both local prediction and authoritative pose updates.
/// </summary>
[RequireComponents(typeof(CameraComponent))]
[RequiresTransform(typeof(Transform))]
public sealed class NetworkKinematicPawnComponent : PawnComponent
{
    private bool _forward;
    private bool _backward;
    private bool _left;
    private bool _right;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        CameraComponent? camera = GetSiblingComponent<CameraComponent>(true);
        if (camera is null)
            return;

        camera.SetPerspective(60.0f, 0.1f, 1000.0f);
        CameraComponent = camera;
    }

    protected override void OnComponentDeactivated()
    {
        ResetMovement();
        base.OnComponentDeactivated();
    }

    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool changing = base.OnPropertyChanging(propName, field, @new);
        if (changing && propName == nameof(Controller))
            ResetMovement();
        return changing;
    }

    public override void RegisterInput(object inputInterface)
    {
        if (inputInterface is not InputInterface input)
            return;

        if (input.Unregister)
            ResetMovement();

        input.RegisterKeyStateChange(EKey.W, OnForward);
        input.RegisterKeyStateChange(EKey.S, OnBackward);
        input.RegisterKeyStateChange(EKey.A, OnLeft);
        input.RegisterKeyStateChange(EKey.D, OnRight);
    }

    public override CharacterPawnInputSnapshot CaptureNetworkInputState()
    {
        if (Controller?.FocusedInteractable is not null || RuntimeInputServices.Current.IsUIInputCaptured)
            ResetMovement();

        // The managed kinematic simulator maps Movement.Y directly onto world +Z.
        // This camera faces -Z, so W must send a negative Y value. Prediction
        // retains each input in history, so every capture owns a new snapshot.
        return new CharacterPawnInputSnapshot
        {
            Movement = new Vector2((_right ? 1.0f : 0.0f) - (_left ? 1.0f : 0.0f),
                (_backward ? 1.0f : 0.0f) - (_forward ? 1.0f : 0.0f)),
        };
    }

    private void OnForward(bool pressed) => _forward = pressed;
    private void OnBackward(bool pressed) => _backward = pressed;
    private void OnLeft(bool pressed) => _left = pressed;
    private void OnRight(bool pressed) => _right = pressed;

    private void ResetMovement()
    {
        _forward = false;
        _backward = false;
        _left = false;
        _right = false;
    }
}
