using System.Numerics;
using XREngine.Components;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine;

/// <summary>Composes Runtime.Core pawns with concrete input, rendering, and UI types.</summary>
internal sealed class EngineRuntimePawnHostServices : IRuntimePawnHostServices
{
    private readonly Dictionary<PawnComponent, OptionalInputRegistration> _optionalRegistrations = [];

    public object? GetLocalInput(PawnComponent pawn) => pawn.Controller?.InputDevice as LocalInputInterface;
    public object? GetGamepad(PawnComponent pawn) => (GetLocalInput(pawn) as LocalInputInterface)?.Gamepad;
    public object? GetKeyboard(PawnComponent pawn) => (GetLocalInput(pawn) as LocalInputInterface)?.Keyboard;
    public object? GetMouse(PawnComponent pawn) => (GetLocalInput(pawn) as LocalInputInterface)?.Mouse;

    public Vector2 GetCursorPositionScreen(PawnComponent pawn)
        => (GetMouse(pawn) as BaseMouse)?.CursorPosition ?? Vector2.Zero;

    public Vector2 GetCursorPositionViewport(PawnComponent pawn)
        => pawn.Viewport is XRViewport viewport
            ? viewport.ScreenToViewportCoordinate(GetCursorPositionScreen(pawn))
            : Vector2.Zero;

    public Vector2 GetCursorPositionInternalCoordinates(PawnComponent pawn)
        => pawn.Viewport is XRViewport viewport
            ? viewport.ViewportToInternalCoordinate(GetCursorPositionViewport(pawn))
            : Vector2.Zero;

    public Segment GetCursorPositionWorld(PawnComponent pawn)
        => pawn.Viewport is XRViewport viewport
            ? viewport.GetWorldSegment(GetCursorPositionViewport(pawn))
            : new Segment(Vector3.Zero, Vector3.Zero);

    public void RegisterControllerInput(PawnComponent pawn, object inputInterface)
    {
        if (inputInterface is not InputInterface input)
            return;

        pawn.RegisterInput(input);
        RegisterOptionalInputs(pawn, input);
        (pawn.UserInterfaceInput as UICanvasInputComponent)?.RegisterInput(input);
    }

    public void RegisterOptionalInputs(PawnComponent pawn, object inputInterface)
    {
        if (inputInterface is not InputInterface input)
            return;

        if (_optionalRegistrations.Remove(pawn, out OptionalInputRegistration? previous))
            previous.Dispose(input.Unregister ? input : null);

        if (input.Unregister)
            return;

        OptionalInputSetComponent[] sets = pawn.GetSiblingComponents<OptionalInputSetComponent>()
            .Concat(pawn.OptionalInputSets.OfType<OptionalInputSetComponent>())
            .Distinct()
            .ToArray();
        OptionalInputRegistration registration = new(this, pawn, input, sets);
        _optionalRegistrations.Add(pawn, registration);
        registration.Register();
    }

    public XRComponent? ResolveCamera(PawnComponent pawn, XRComponent? configuredCamera)
    {
        if (configuredCamera is CameraComponent { IsDestroyed: false, SceneNode: { IsDestroyed: false } } camera)
            return camera;

        CameraComponent? sibling = pawn.GetSiblingComponent<CameraComponent>();
        if (sibling is { IsDestroyed: false, SceneNode: { IsDestroyed: false } })
            return sibling;

        return configuredCamera is { IsDestroyed: false } ? configuredCamera : null;
    }

    private void RefreshOptionalInputs(PawnComponent pawn, InputInterface input)
    {
        input.Unregister = true;
        RegisterOptionalInputs(pawn, input);
        input.Unregister = false;
        RegisterOptionalInputs(pawn, input);
    }

    private sealed class OptionalInputRegistration
    {
        private readonly EngineRuntimePawnHostServices _owner;
        private readonly PawnComponent _pawn;
        private readonly InputInterface _input;
        private readonly OptionalInputSetComponent[] _sets;
        private readonly SceneNode? _sceneNode;
        private readonly XRPropertyChangedEventHandler _activeChanged;
        private readonly TCollectionChangedEventHandler<XRComponent> _collectionChanged;

        public OptionalInputRegistration(
            EngineRuntimePawnHostServices owner,
            PawnComponent pawn,
            InputInterface input,
            OptionalInputSetComponent[] sets)
        {
            _owner = owner;
            _pawn = pawn;
            _input = input;
            _sets = sets;
            _sceneNode = pawn.SceneNode;
            _activeChanged = OnActiveChanged;
            _collectionChanged = OnCollectionChanged;
        }

        public void Register()
        {
            foreach (OptionalInputSetComponent set in _sets)
            {
                set.PropertyChanged += _activeChanged;
                if (set.IsActive)
                    set.RegisterInput(_input);
            }

            if (_pawn.SceneNode is { } node)
                node.Components.CollectionChanged += _collectionChanged;
            _pawn.OptionalInputSets.CollectionChanged += _collectionChanged;
        }

        public void Dispose(InputInterface? unregisterInput)
        {
            foreach (OptionalInputSetComponent set in _sets)
            {
                set.PropertyChanged -= _activeChanged;
                if (unregisterInput is not null && set.IsActive)
                    set.RegisterInput(unregisterInput);
            }

            if (_pawn.SceneNode is { } node)
                node.Components.CollectionChanged -= _collectionChanged;
            _pawn.OptionalInputSets.CollectionChanged -= _collectionChanged;
        }

        private void OnActiveChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(XRComponent.IsActive) || sender is not OptionalInputSetComponent set)
                return;

            if (set.IsActive)
            {
                set.RegisterInput(_input);
                return;
            }

            _input.Unregister = true;
            set.RegisterInput(_input);
            _input.Unregister = false;
        }

        private void OnCollectionChanged(object sender, TCollectionChangedEventArgs<XRComponent> e)
            => _owner.RefreshOptionalInputs(_pawn, _input);
    }
}
