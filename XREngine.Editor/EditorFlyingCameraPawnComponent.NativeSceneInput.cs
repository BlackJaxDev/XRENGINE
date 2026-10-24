using System.Numerics;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene.Components.Editing;

namespace XREngine.Editor;

public partial class EditorFlyingCameraPawnComponent
{
    private XRViewport? _lastWorldPickViewport;
    private int _worldPickOwnerRevision;
    private int _activeWorldPickOwnerRevision;
    private ScenePickOwner _activeWorldPickOwner;
    private ScenePickOwner? _pendingClickOwner;
    private ScenePickOwner? _selectionDragOwner;
    private NativeScenePointerOwner? _rightDragOwner;
    private bool _selectionSubscriptionActive;
    private bool _nativeSceneDestroying;

    /// <summary>Only the primary editor pawn owns the global selection gizmo lifecycle.</summary>
    public bool OwnsGlobalSelectionGizmo { get; set; } = true;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (OwnsGlobalSelectionGizmo && !_selectionSubscriptionActive && !_nativeSceneDestroying)
        {
            Selection.SelectionChanged += Selection_SelectionChanged;
            _selectionSubscriptionActive = true;
            Selection_SelectionChanged(Selection.SceneNodes);
        }
    }

    private bool IsCurrentWorldPickOwner(int revision, in ScenePickOwner owner)
        => !_nativeSceneDestroying && !IsDestroyed && !IsDestroyQueued &&
            Volatile.Read(ref _worldPickOwnerRevision) == revision && owner.IsCurrent(this, true);

    private bool TryGetNativeScenePointer(out XRViewport viewport, out Vector2 normalizedPoint)
    {
        if (TryGetNativeScenePointer(out NativeScenePointerOwner owner, out normalizedPoint))
        {
            viewport = owner.Viewport;
            return true;
        }

        viewport = null!;
        normalizedPoint = default;
        return false;
    }

    private bool TryGetNativeScenePointer(out NativeScenePointerOwner owner, out Vector2 normalizedPoint)
    {
        for (int index = 0; index < LinkedUICanvasInputs.Count; index++)
        {
            XRComponent component = LinkedUICanvasInputs[index];
            if (component is UICanvasInputComponent input &&
                input.TryGetPresentedPointerTarget(out var target, out normalizedPoint,
                    out var identity, out long revision, out ulong lifetime) &&
                target is NativeSceneViewportInput scene &&
                scene.TrySample(this, input, normalizedPoint, identity, revision, lifetime, out owner))
                return true;
        }
        owner = default;
        normalizedPoint = default;
        return false;
    }

    private bool TryCaptureScenePickOwner(XRViewport viewport, out ScenePickOwner owner)
    {
        owner = default;
        if (_nativeSceneDestroying || IsDestroyed || IsDestroyQueued ||
            viewport.Window is not { } window || viewport.CameraComponent is not { } camera ||
            viewport.World is not { } world)
            return false;
        NativeScenePointerOwner? native = TryGetNativeScenePointer(out NativeScenePointerOwner scene, out _) &&
            ReferenceEquals(scene.Viewport, viewport) ? scene : null;
        if (native is null && !ReferenceEquals(viewport, Viewport))
            return false;
        owner = new(viewport, window, camera, world, native);
        return owner.IsCurrent(this, false);
    }

    private void ValidateRightSceneDrag()
    {
        if (_rightDragOwner is { } owner && !owner.IsCurrent(this, false))
        {
            _rightDragOwner = null;
            CancelRightClickDrag();
        }
    }

    protected override void OnComponentDeactivated()
    {
        try
        {
            RetireSceneInteraction();
        }
        finally
        {
            base.OnComponentDeactivated();
        }
    }

    protected override void OnDestroying()
    {
        _nativeSceneDestroying = true;
        try
        {
            RetireSceneInteraction();
        }
        finally
        {
            base.OnDestroying();
        }
    }

    private void RetireSceneInteraction()
    {
        if (_selectionSubscriptionActive)
        {
            Selection.SelectionChanged -= Selection_SelectionChanged;
            _selectionSubscriptionActive = false;
        }
        _rightDragOwner = null;
        CancelRightClickDrag();
        if (_selectionDragActive)
            ResetSelectionDrag();
        _pendingClickOwner = null;
        _selectAfterForcedPick = false;
        Interlocked.Increment(ref _worldPickOwnerRevision);
        UpdateWorldPickInputState(new(float.NaN, float.NaN), false);
        if (TransformTool3D.GetActiveInstance(out TransformTool3D? tool) && tool is not null)
            tool.CancelPointerInteraction(this);
    }
}
