using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Input.Devices;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>
/// Provides shared drag/drop state for the editor's UI canvas.
/// </summary>
public static class EditorDragDropUtility
{
    public const float DefaultActivationDistance = 8.0f;
    public const string SceneNodePayloadType = "SceneNode";
    public const string AssetPathPayloadType = "AssetPath";

    private static UICanvasInputComponent? _inputComponent;
    private static DragSession? _activeSession;
    private static DropTargetRegistration? _hoveredTarget;
    private static readonly List<DropTargetRegistration> _dropTargets = [];

    public static bool IsInitialized => _inputComponent is not null;
    public static bool IsDragging => _activeSession is not null;
    public static bool IsLeftMouseButtonHeld => TryGetInput()?.GetMouseButtonState(EMouseButton.LeftClick, EButtonInputType.Held) ?? false;

    public static void Initialize(UICanvasInputComponent? input)
    {
        if (_inputComponent == input)
            return;

        if (_inputComponent is not null)
            Engine.Time.Timer.SwapBuffers -= OnSwapBuffers;

        _inputComponent = input;

        if (_inputComponent is not null)
            Engine.Time.Timer.SwapBuffers += OnSwapBuffers;
    }

    // --- Scene node payloads ---

    public static DragPayload CreateSceneNodePayload(SceneNode node)
        => new(SceneNodePayloadType, node);

    public static bool IsSceneNodePayload(in DragPayload payload)
        => payload.TypeId == SceneNodePayloadType && payload.Data is SceneNode;

    // --- Asset path payloads (Phase 8B) ---

    public static DragPayload CreateAssetPayload(string path)
        => new(AssetPathPayloadType, path);

    public static bool IsAssetPathPayload(in DragPayload payload)
        => payload.TypeId == AssetPathPayloadType && payload.Data is string;

    public static bool TryGetAssetPath(in DragPayload payload, out string? path)
    {
        if (payload.TypeId == AssetPathPayloadType && payload.Data is string s)
        {
            path = s;
            return true;
        }

        path = null;
        return false;
    }

    public static bool TryGetActivePayload(out DragPayload payload)
    {
        if (_activeSession is null)
        {
            payload = default;
            return false;
        }

        payload = _activeSession.Payload;
        return true;
    }

    public static bool TryGetActiveSceneNode(out SceneNode? node)
    {
        node = null;
        return _activeSession is not null && TryGetSceneNode(_activeSession.Payload, out node) && node is not null;
    }

    public static bool TryGetSceneNode(in DragPayload payload, out SceneNode? node)
    {
        if (payload.TypeId == SceneNodePayloadType && payload.Data is SceneNode sceneNode)
        {
            node = sceneNode;
            return true;
        }

        node = null;
        return false;
    }

    public static bool BeginDrag(UIInteractableComponent source, DragPayload payload, Vector2 startCanvasPosition)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        if (!IsInitialized || !IsLeftMouseButtonHeld)
            return false;

        if (_activeSession is not null)
            return false;

        _activeSession = new DragSession(source, payload, startCanvasPosition);
        SetHoveredTarget(null);
        return true;
    }

    public static IDisposable RegisterDropTarget(
        UIBoundableTransform transform,
        Func<DragPayload, bool> onDrop,
        Func<DragPayload, bool>? canAccept = null,
        Action<bool>? hoverChanged = null)
    {
        if (transform is null)
            throw new ArgumentNullException(nameof(transform));
        if (onDrop is null)
            throw new ArgumentNullException(nameof(onDrop));

        var registration = new DropTargetRegistration(transform, onDrop, canAccept, hoverChanged);
        _dropTargets.Add(registration);
        return new DropTargetHandle(registration);
    }

    private static void OnSwapBuffers()
    {
        using var sample = Engine.Profiler.Start("EditorDragDropUtility.OnSwapBuffers");
        
        CleanupTargets();

        if (_activeSession is null)
            return;

        UpdateHoveredTarget();

        if (!IsLeftMouseButtonHeld)
            CompleteDrag();
    }

    private static void CompleteDrag()
    {
        if (_activeSession is null)
            return;

        var payload = _activeSession.Payload;
        bool handled = false;
        var target = _hoveredTarget;
        if (target is not null && target.IsValid && target.CanAccept(payload))
            handled = target.TryDrop(payload);

        _activeSession = null;
        SetHoveredTarget(null);

        if (!handled)
            CancelHoverState();
    }

    private static void CancelHoverState()
        => _hoveredTarget?.SetHover(false);

    private static void UpdateHoveredTarget()
    {
        if (!IsInitialized || _activeSession is null)
            return;

        Vector2 cursor = _inputComponent!.CursorPositionWorld2D;
        DropTargetRegistration? candidate = null;
        var payload = _activeSession.Payload;

        for (int i = _dropTargets.Count - 1; i >= 0; i--)
        {
            var registration = _dropTargets[i];
            if (!registration.IsValid)
                continue;

            if (!registration.CanAccept(payload))
                continue;

            if (registration.Contains(cursor))
            {
                candidate = registration;
                break;
            }
        }

        SetHoveredTarget(candidate);
    }

    private static void SetHoveredTarget(DropTargetRegistration? target)
    {
        if (_hoveredTarget == target)
            return;

        _hoveredTarget?.SetHover(false);
        _hoveredTarget = target;
        _hoveredTarget?.SetHover(true);
    }

    private static void CleanupTargets()
    {
        for (int i = _dropTargets.Count - 1; i >= 0; i--)
        {
            if (!_dropTargets[i].IsValid)
            {
                if (_hoveredTarget == _dropTargets[i])
                    SetHoveredTarget(null);
                _dropTargets.RemoveAt(i);
            }
        }
    }

    private static InputInterface? TryGetInput()
        => _inputComponent?.OwningPawn?.LocalPlayerController?.Input;

    private sealed record DragSession(UIInteractableComponent Source, DragPayload Payload, Vector2 StartPosition);

    public readonly struct DragPayload
    {
        public DragPayload(string typeId, object data)
        {
            TypeId = typeId ?? throw new ArgumentNullException(nameof(typeId));
            Data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public string TypeId { get; }
        public object Data { get; }
    }

    private sealed class DropTargetRegistration
    {
        private readonly WeakReference<UIBoundableTransform> _transformRef;
        private readonly Func<DragPayload, bool> _onDrop;
        private readonly Func<DragPayload, bool>? _canAccept;
        private readonly Action<bool>? _hoverChanged;
        private bool _hovering;
        private bool _disposed;

        public DropTargetRegistration(
            UIBoundableTransform transform,
            Func<DragPayload, bool> onDrop,
            Func<DragPayload, bool>? canAccept,
            Action<bool>? hoverChanged)
        {
            _transformRef = new WeakReference<UIBoundableTransform>(transform);
            _onDrop = onDrop;
            _canAccept = canAccept;
            _hoverChanged = hoverChanged;
        }

        public bool IsValid
        {
            get
            {
                if (_disposed)
                    return false;
                if (!_transformRef.TryGetTarget(out var transform))
                    return false;
                return transform.SceneNode is not null;
            }
        }

        public bool Contains(Vector2 canvasPoint)
        {
            if (!_transformRef.TryGetTarget(out var transform))
                return false;

            Vector2 local = transform.CanvasToLocal(canvasPoint);
            BoundingRectangleF bounds = transform.GetActualBounds();
            return bounds.Contains(local);
        }

        public bool CanAccept(DragPayload payload)
        {
            if (_disposed)
                return false;
            return _canAccept?.Invoke(payload) ?? true;
        }

        public bool TryDrop(DragPayload payload)
        {
            if (_disposed)
                return false;
            return _onDrop(payload);
        }

        public void SetHover(bool hovering)
        {
            if (_hovering == hovering)
                return;

            _hovering = hovering;
            _hoverChanged?.Invoke(hovering);
        }

        public void Dispose()
        {
            _disposed = true;
            _hoverChanged?.Invoke(false);
        }
    }

    private sealed class DropTargetHandle : IDisposable
    {
        private readonly DropTargetRegistration _registration;
        private bool _disposed;

        public DropTargetHandle(DropTargetRegistration registration)
            => _registration = registration;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _registration.Dispose();
        }
    }
}
