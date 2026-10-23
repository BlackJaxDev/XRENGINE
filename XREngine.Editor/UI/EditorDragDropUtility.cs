using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
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
    private static readonly List<DragSession> _sessionScratch = [];
    private static readonly List<DropTargetRegistration> _dropTargets = [];
    private static bool _tickSubscribed;
    private static bool _updatingSessions;

    public static bool IsInitialized => _inputComponent is not null;
    public static bool IsDragging => _sessions.Count != 0;
    public static bool IsLeftMouseButtonHeld => TryGetInput()?.GetMouseButtonState(EMouseButton.LeftClick, EButtonInputType.Pressed) ?? false;
    public static bool IsLeftMouseButtonHeldFor(UIInteractableComponent source)
        => IsLeftMouseButtonHeldFor(ResolveCanvasInput(source));

    public static void Initialize(UICanvasInputComponent? input)
    {
        if (_inputComponent == input)
            return;

        _inputComponent = input;
        if (input is not null)
            EnsureTickSubscribed();
        else
            StopTickIfUnused();
    }

    // --- Scene node payloads ---

    public static DragPayload CreateSceneNodePayload(SceneNode node)
        => new(SceneNodePayloadType, node);

    public static bool IsSceneNodePayload(in DragPayload payload)
        => payload.TypeId == SceneNodePayloadType && payload.Data is SceneNode;

    // --- Asset path payloads ---

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
        if (input is null || !source.CanInteract || !IsLeftMouseButtonHeldFor(input))
            return false;

        if (_sessions.ContainsKey(input))
            return false;

        UICanvasComponent? canvas = source.BoundableTransform.GetCanvasComponent();
        if (canvas is null)
            return false;
        EnsureTickSubscribed();
        _sessions.Add(input, new DragSession(source, input, canvas, payload, startCanvasPosition));
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
        if (_updatingSessions)
            return;
        _updatingSessions = true;
        try
        {
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("EditorDragDropUtility.OnPostUpdateFrame");
            CleanupTargets();
            _sessionScratch.Clear();
            foreach (DragSession session in _sessions.Values)
                _sessionScratch.Add(session);

            for (int i = 0; i < _sessionScratch.Count; i++)
            {
                DragSession session = _sessionScratch[i];
                if (!IsRegisteredSession(session))
                    continue;
                if (!session.IsCurrent)
                {
                    CancelDrag(session);
                    continue;
                }
                UpdateHoveredTarget(session);
                if (!IsRegisteredSession(session) || !session.IsCurrent)
                {
                    CancelDrag(session);
                    continue;
                }
                if (!IsLeftMouseButtonHeldFor(session.Input))
                    CompleteDrag(session);
            }
        }
        finally
        {
            _sessionScratch.Clear();
            _updatingSessions = false;
            StopTickIfUnused();
        }
    }

    private static void CompleteDrag(DragSession session)
    {
        if (!IsRegisteredSession(session) || !session.IsCurrent)
        {
            CancelDrag(session);
            return;
        }
        if (!RemoveSession(session))
            return;

        DragPayload payload = session.Payload;
        DropTargetRegistration? target = session.HoveredTarget;
        SetHoveredTarget(session, null);
        if (target is not null && target.IsValid && session.IsCurrent &&
            target.CanAccept(payload) && session.IsCurrent && target.IsValid)
            target.TryDrop(payload);
        StopTickIfUnused();
    }

    private static void CancelDrag(DragSession session)
    {
        RemoveSession(session);
        SetHoveredTarget(session, null);
        StopTickIfUnused();
    }

    private static bool IsRegisteredSession(DragSession session)
        => _sessions.TryGetValue(session.Input, out DragSession? current) &&
            ReferenceEquals(current, session);

    private static bool RemoveSession(DragSession session)
        => IsRegisteredSession(session) && _sessions.Remove(session.Input);

    private static void UpdateHoveredTarget(DragSession session)
    {
        Vector2 cursor = session.Input.CursorPositionWorld2D;
        DropTargetRegistration? candidate = null;
        DragPayload payload = session.Payload;

        for (int i = _dropTargets.Count - 1; i >= 0; i--)
        {
            if (i >= _dropTargets.Count)
                continue;
            var registration = _dropTargets[i];
            if (!registration.IsValid || !ReferenceEquals(registration.Canvas, session.Canvas))
                continue;

            if (!registration.CanAccept(payload))
                continue;

            if (registration.IsValid && _dropTargets.Contains(registration) &&
                registration.Contains(cursor))
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

        DropTargetRegistration? previous = session.HoveredTarget;
        session.HoveredTarget = target;
        previous?.SetHover(false);
        if (ReferenceEquals(session.HoveredTarget, target))
            target?.SetHover(true);
    }

    private static void CleanupTargets()
    {
        for (int i = _dropTargets.Count - 1; i >= 0; i--)
        {
            if (i >= _dropTargets.Count)
                continue;
            DropTargetRegistration registration = _dropTargets[i];
            if (registration.IsValid)
                continue;
            while (FindSessionHovering(registration) is { } session)
                SetHoveredTarget(session, null);
            _dropTargets.Remove(registration);
        }
    }

    private static DragSession? FindSessionHovering(DropTargetRegistration target)
    {
        foreach (DragSession session in _sessions.Values)
            if (ReferenceEquals(session.HoveredTarget, target))
                return session;
        return null;
    }

    private static InputInterface? TryGetInput()
        => _inputComponent?.OwningPawn?.Controller?.InputDevice as InputInterface;

    private static DragSession? GetPreferredSession()
    {
        while (true)
        {
            if (_inputComponent is not null &&
                _sessions.TryGetValue(_inputComponent, out DragSession? primary))
            {
                if (primary.IsCurrent)
                    return primary;
                CancelDrag(primary);
                continue;
            }
            DragSession? invalid = null;
            foreach (DragSession session in _sessions.Values)
            {
                if (session.IsCurrent)
                    return session;
                invalid = session;
                break;
            }
            if (invalid is null)
                return null;
            CancelDrag(invalid);
        }
    }

    private static UICanvasInputComponent? ResolveCanvasInput(UIInteractableComponent source)
    {
        UICanvasComponent? canvas = source.BoundableTransform.GetCanvasComponent();
        return canvas is null ? _inputComponent : canvas.GetInputComponent() as UICanvasInputComponent;
    }

    private static bool IsLeftMouseButtonHeldFor(UICanvasInputComponent? input)
        => (input?.OwningPawn?.Controller?.InputDevice as InputInterface)?
            .GetMouseButtonState(EMouseButton.LeftClick, EButtonInputType.Pressed) ?? false;

    private static void EnsureTickSubscribed()
    {
        if (_tickSubscribed)
            return;
        RuntimeRenderingHostServices.Scheduling.SubscribePostUpdateFrame(OnPostUpdateFrame);
        _tickSubscribed = true;
    }

    private static void StopTickIfUnused()
    {
        if (!_tickSubscribed || _inputComponent is not null || _sessions.Count != 0)
            return;
        CleanupTargets();
        if (_inputComponent is not null || _sessions.Count != 0)
            return;
        RuntimeRenderingHostServices.Scheduling.UnsubscribePostUpdateFrame(OnPostUpdateFrame);
        _tickSubscribed = false;
    }

    private sealed class DragSession(UIInteractableComponent source, UICanvasInputComponent input,
        UICanvasComponent canvas, DragPayload payload, Vector2 startPosition)
    {
        public UIInteractableComponent Source { get; } = source;
        public UICanvasInputComponent Input { get; } = input;
        public UICanvasComponent Canvas { get; } = canvas;
        public long SourceRevision { get; } = source.InteractionRevision;
        public UIElementIdentity SourceIdentity { get; } = source.AccessibilityIdentity;
        public bool IsCurrent => IsCurrentDragSource(Source, Input, Canvas,
            SourceRevision, SourceIdentity);
        public DragPayload Payload { get; } = payload;
        public Vector2 StartPosition { get; } = startPosition;
        public DropTargetRegistration? HoveredTarget { get; set; }
    }

    internal static bool IsCurrentDragSource(UIInteractableComponent source,
        UICanvasInputComponent input, UICanvasComponent canvas,
        long revision, UIElementIdentity identity)
        => source.SceneNode is { IsActiveInHierarchy: true } && source.CanInteract &&
            input.IsActiveInHierarchy && canvas.IsActiveInHierarchy &&
            source.InteractionRevision == revision && source.AccessibilityIdentity == identity &&
            ReferenceEquals(source.BoundableTransform.GetCanvasComponent(), canvas) &&
            ReferenceEquals(canvas.GetInputComponent(), input);

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
