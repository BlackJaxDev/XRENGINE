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
    public const string FilePathPayloadType = "FilePath";
    public const string ComponentPayloadType = "Component";
    private const string EditorPayloadPrefix = "Editor:";

    private static UICanvasInputComponent? _inputComponent;
    private static readonly Dictionary<UICanvasInputComponent, DragSession> _sessions = [];
    private static readonly List<DropTargetRegistration> _dropTargets = [];
    private static bool _tickSubscribed;

    public static bool IsInitialized => _inputComponent is not null;
    public static bool IsDragging => _sessions.Count != 0;
    public static bool IsLeftMouseButtonHeld => TryGetInput()?.GetMouseButtonState(EMouseButton.LeftClick, EButtonInputType.Held) ?? false;
    public static bool IsLeftMouseButtonHeldFor(UIInteractableComponent source)
        => IsLeftMouseButtonHeldFor(ResolveCanvasInput(source));

    public static void Initialize(UICanvasInputComponent? input)
    {
        if (_inputComponent == input)
            return;

        _inputComponent = input;
        if (input is not null)
            EnsureTickSubscribed();
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

    public static DragPayload CreateFilePayload(string path)
        => new(FilePathPayloadType, Path.GetFullPath(path));

    public static bool TryGetFilePath(in DragPayload payload, out string? path)
    {
        path = payload.TypeId == FilePathPayloadType ? payload.Data as string : null;
        return path is not null;
    }

    public static bool TryGetAssetOrFilePath(in DragPayload payload, out string? path)
        => TryGetAssetPath(payload, out path) || TryGetFilePath(payload, out path);

    public static DragPayload CreateComponentPayload(XRComponent component)
        => new(ComponentPayloadType, component);

    public static bool TryGetComponent(in DragPayload payload, out XRComponent? component)
    {
        component = payload.TypeId == ComponentPayloadType ? payload.Data as XRComponent : null;
        return component is { IsDestroyed: false };
    }

    public static DragPayload CreateEditorPayload<T>(string key, T value) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new(EditorPayloadPrefix + key, value);
    }

    public static bool TryGetEditorPayload<T>(in DragPayload payload, string key, out T? value) where T : class
    {
        value = payload.TypeId == EditorPayloadPrefix + key ? payload.Data as T : null;
        return value is not null;
    }

    public static bool TryGetActivePayload(out DragPayload payload)
    {
        DragSession? session = GetPreferredSession();
        if (session is null)
        {
            payload = default;
            return false;
        }

        payload = session.Payload;
        return true;
    }

    public static bool TryGetActiveSceneNode(out SceneNode? node)
    {
        node = null;
        DragSession? session = GetPreferredSession();
        return session is not null && TryGetSceneNode(session.Payload, out node) && node is not null;
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

        UICanvasInputComponent? input = ResolveCanvasInput(source);
        if (input is null || !IsLeftMouseButtonHeldFor(input))
            return false;

        if (_sessions.ContainsKey(input))
            return false;

        EnsureTickSubscribed();
        _sessions.Add(input, new DragSession(source, input, source.BoundableTransform.GetCanvasComponent(), payload, startCanvasPosition));
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

    private static void OnPostUpdateFrame()
    {
        using var sample = Engine.Profiler.Start("EditorDragDropUtility.OnPostUpdateFrame");
        
        CleanupTargets();

        foreach (DragSession session in _sessions.Values.ToArray())
        {
            UpdateHoveredTarget(session);
            if (!IsLeftMouseButtonHeldFor(session.Input))
                CompleteDrag(session);
        }
    }

    private static void CompleteDrag(DragSession session)
    {
        if (!_sessions.Remove(session.Input))
            return;

        DragPayload payload = session.Payload;
        DropTargetRegistration? target = session.HoveredTarget;
        SetHoveredTarget(session, null);
        if (target is not null && target.IsValid && target.CanAccept(payload))
            target.TryDrop(payload);
    }

    private static void UpdateHoveredTarget(DragSession session)
    {
        Vector2 cursor = session.Input.CursorPositionWorld2D;
        DropTargetRegistration? candidate = null;
        DragPayload payload = session.Payload;

        for (int i = _dropTargets.Count - 1; i >= 0; i--)
        {
            var registration = _dropTargets[i];
            if (!registration.IsValid || !ReferenceEquals(registration.Canvas, session.Canvas))
                continue;

            if (!registration.CanAccept(payload))
                continue;

            if (registration.Contains(cursor))
            {
                candidate = registration;
                break;
            }
        }

        SetHoveredTarget(session, candidate);
    }

    private static void SetHoveredTarget(DragSession session, DropTargetRegistration? target)
    {
        if (session.HoveredTarget == target)
            return;

        session.HoveredTarget?.SetHover(false);
        session.HoveredTarget = target;
        session.HoveredTarget?.SetHover(true);
    }

    private static void CleanupTargets()
    {
        for (int i = _dropTargets.Count - 1; i >= 0; i--)
        {
            if (!_dropTargets[i].IsValid)
            {
                foreach (DragSession session in _sessions.Values)
                    if (session.HoveredTarget == _dropTargets[i])
                        SetHoveredTarget(session, null);
                _dropTargets.RemoveAt(i);
            }
        }
    }

    private static InputInterface? TryGetInput()
        => _inputComponent?.OwningPawn?.Controller?.InputDevice as InputInterface;

    private static DragSession? GetPreferredSession()
    {
        if (_inputComponent is not null && _sessions.TryGetValue(_inputComponent, out DragSession? primary))
            return primary;
        return _sessions.Values.FirstOrDefault();
    }

    private static UICanvasInputComponent? ResolveCanvasInput(UIInteractableComponent source)
    {
        UICanvasComponent? canvas = source.BoundableTransform.GetCanvasComponent();
        return canvas is null ? _inputComponent : canvas.GetInputComponent() as UICanvasInputComponent;
    }

    private static bool IsLeftMouseButtonHeldFor(UICanvasInputComponent? input)
        => (input?.OwningPawn?.Controller?.InputDevice as InputInterface)?
            .GetMouseButtonState(EMouseButton.LeftClick, EButtonInputType.Held) ?? false;

    private static void EnsureTickSubscribed()
    {
        if (_tickSubscribed)
            return;
        Engine.Time.Timer.PostUpdateFrame += OnPostUpdateFrame;
        _tickSubscribed = true;
    }

    private sealed class DragSession(UIInteractableComponent source, UICanvasInputComponent input,
        UICanvasComponent? canvas, DragPayload payload, Vector2 startPosition)
    {
        public UIInteractableComponent Source { get; } = source;
        public UICanvasInputComponent Input { get; } = input;
        public UICanvasComponent? Canvas { get; } = canvas;
        public DragPayload Payload { get; } = payload;
        public Vector2 StartPosition { get; } = startPosition;
        public DropTargetRegistration? HoveredTarget { get; set; }
    }

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
        public UICanvasComponent? Canvas => _transformRef.TryGetTarget(out var transform)
            ? transform.GetCanvasComponent() : null;
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
                return transform.SceneNode is { IsActiveInHierarchy: true };
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
