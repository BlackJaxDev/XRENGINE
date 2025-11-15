using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using XREngine;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Input.Devices;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>
/// Captures property edits on <see cref="XRBase"/> objects and provides undo/redo support for the editor.
/// </summary>
public static class Undo
{
    private static readonly object _sync = new();
    private static readonly Dictionary<XRBase, TrackedObject> _trackedObjects = new(ReferenceEqualityComparer.Instance);
    private static readonly Stack<ChangeScope> _scopeStack = new();
    private static readonly Stack<UndoAction> _undoStack = new();
    private static readonly Stack<UndoAction> _redoStack = new();
    private static readonly Dictionary<LocalInputInterface, ShortcutHandlers> _shortcutHandlers = new(ReferenceEqualityComparer.Instance);
    private static readonly AsyncLocal<UserInteractionContext?> _userInteractionContext = new();
    private static int _activeUserInteractionCount;

    private static int _suppressRecordingCount;
    private static bool _initialized;
    private static readonly ConcurrentQueue<TransformBase> _pendingTransformRefresh = new();
    private static int _transformRefreshHooked;

    public static event Action? HistoryChanged;

    public static bool CanUndo
    {
        get
        {
            lock (_sync)
                return _undoStack.Count > 0;
        }
    }

    public static bool CanRedo
    {
        get
        {
            lock (_sync)
                return _redoStack.Count > 0;
        }
    }

    public static IReadOnlyList<UndoEntry> PendingUndo
    {
        get
        {
            lock (_sync)
                return Snapshot(_undoStack);
        }
    }

    public static IReadOnlyList<UndoEntry> PendingRedo
    {
        get
        {
            lock (_sync)
                return Snapshot(_redoStack);
        }
    }

    public static void Initialize()
    {
        lock (_sync)
        {
            if (_initialized)
                return;

            LocalInputInterface.GlobalRegisters.Add(RegisterGlobalInput);
            EnsureTransformRefreshHooked();
            _initialized = true;
        }
    }

    public static ChangeScope BeginChange(string description)
    {
        var scope = new ChangeScope(string.IsNullOrWhiteSpace(description) ? "Change" : description.Trim());
        lock (_sync)
            _scopeStack.Push(scope);
        return scope;
    }

    public static IDisposable BeginUserInteraction()
    {
        var context = _userInteractionContext.Value;
        if (context is null)
        {
            context = new UserInteractionContext();
            _userInteractionContext.Value = context;
        }

        context.Depth++;
        Interlocked.Increment(ref _activeUserInteractionCount);
        return new UserInteractionScope(context);
    }

    public static bool TryUndo()
    {
        FlushPendingScopes();

        UndoAction? action;
        lock (_sync)
        {
            if (_undoStack.Count == 0)
                return false;

            action = _undoStack.Pop();
            _redoStack.Push(action);
        }

        ApplyUndo(action);
        RaiseHistoryChanged();
        return true;
    }

    public static bool TryRedo()
    {
        FlushPendingScopes();

        UndoAction? action;
        lock (_sync)
        {
            if (_redoStack.Count == 0)
                return false;

            action = _redoStack.Pop();
            _undoStack.Push(action);
        }

        ApplyRedo(action);
        RaiseHistoryChanged();
        return true;
    }

    public static void ClearHistory()
    {
        lock (_sync)
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        RaiseHistoryChanged();
    }

    public static void Track(XRBase? instance)
    {
        if (instance is null)
            return;

        lock (_sync)
        {
            if (_trackedObjects.ContainsKey(instance))
                return;

            var context = new TrackedObject(instance);
            instance.PropertyChanged += OnTrackedObjectPropertyChanged;
            context.AddDisposeAction(() => instance.PropertyChanged -= OnTrackedObjectPropertyChanged);
            _trackedObjects.Add(instance, context);
            ConfigureSpecializedTracking(instance, context);
        }
    }

    public static void Untrack(XRBase? instance)
    {
        if (instance is null)
            return;

        TrackedObject? context;
        lock (_sync)
        {
            if (!_trackedObjects.TryGetValue(instance, out context))
                return;

            _trackedObjects.Remove(instance);
        }

        context.Dispose();
    }

    public static void TrackWorld(XRWorld? world)
    {
        if (world is null)
            return;

        Track(world);

        if (world.Settings is not null)
            Track(world.Settings);

        TrackIfXRBase(world.DefaultGameMode);

        foreach (var scene in world.Scenes)
            TrackScene(scene);
    }

    public static void TrackScene(XRScene? scene)
    {
        if (scene is null)
            return;

        Track(scene);
        foreach (var node in scene.RootNodes)
            TrackSceneNode(node);
    }

    public static void TrackSceneNode(SceneNode? node)
    {
        if (node is null)
            return;

        Track(node);
    }

    private static void TrackIfXRBase(object? instance)
    {
        if (instance is XRBase xr)
            Track(xr);
    }

    private static void RegisterGlobalInput(InputInterface inputInterface)
    {
        if (inputInterface is not LocalInputInterface local)
            return;

        lock (_sync)
        {
            if (inputInterface.Unregister)
            {
                if (_shortcutHandlers.TryGetValue(local, out ShortcutHandlers handlers))
                {
                    local.RegisterKeyEvent(EKey.Z, EButtonInputType.Pressed, handlers.OnUndo);
                    local.RegisterKeyEvent(EKey.Y, EButtonInputType.Pressed, handlers.OnRedo);
                    _shortcutHandlers.Remove(local);
                }
                return;
            }

            if (_shortcutHandlers.ContainsKey(local))
                return;

            Action undoHandler = () => HandleShortcut(local, ShortcutKind.Undo);
            Action redoHandler = () => HandleShortcut(local, ShortcutKind.Redo);
            _shortcutHandlers[local] = new ShortcutHandlers(undoHandler, redoHandler);

            local.RegisterKeyEvent(EKey.Z, EButtonInputType.Pressed, undoHandler);
            local.RegisterKeyEvent(EKey.Y, EButtonInputType.Pressed, redoHandler);
        }
    }

    private static void HandleShortcut(LocalInputInterface input, ShortcutKind kind)
    {
        if (!EditorState.InEditMode)
            return;

        if (!IsCtrlDown(input))
            return;

        if (kind == ShortcutKind.Undo)
        {
            if (IsShiftDown(input))
            {
                TryRedo();
                return;
            }

            TryUndo();
        }
        else
        {
            TryRedo();
        }
    }

    private static bool IsCtrlDown(LocalInputInterface input)
        => input.GetKeyState(EKey.ControlLeft, EButtonInputType.Held)
        || input.GetKeyState(EKey.ControlRight, EButtonInputType.Held)
        || input.GetKeyState(EKey.ControlLeft, EButtonInputType.Pressed)
        || input.GetKeyState(EKey.ControlRight, EButtonInputType.Pressed);

    private static bool IsShiftDown(LocalInputInterface input)
        => input.GetKeyState(EKey.ShiftLeft, EButtonInputType.Held)
        || input.GetKeyState(EKey.ShiftRight, EButtonInputType.Held)
        || input.GetKeyState(EKey.ShiftLeft, EButtonInputType.Pressed)
        || input.GetKeyState(EKey.ShiftRight, EButtonInputType.Pressed);

    private static void ConfigureSpecializedTracking(XRBase instance, TrackedObject context)
    {
        switch (instance)
        {
            case SceneNode node:
                var sceneContext = new SceneNodeContext(node);
                context.SceneNodeContext = sceneContext;
                break;
            case TransformBase transform:
                ConfigureTransformTracking(transform, context);
                break;
            case XRComponent component:
                ConfigureComponentTracking(component, context);
                break;
        }
    }

    private static void ConfigureTransformTracking(TransformBase transform, TrackedObject context)
    {
        // Placeholder for transform-specific hooks.
    }

    private static void ConfigureComponentTracking(XRComponent component, TrackedObject context)
    {
        component.Destroyed += ComponentDestroyed;
        context.AddDisposeAction(() => component.Destroyed -= ComponentDestroyed);

        static void ComponentDestroyed(XRObjectBase obj)
        {
            if (obj is XRComponent comp)
                Untrack(comp);
        }
    }

    private static void RecordPropertyChange(XRBase target, string propertyName, object? previousValue, object? newValue)
    {
        if (string.IsNullOrEmpty(propertyName))
            return;

        if (Equals(previousValue, newValue))
            return;

        bool addedImmediate = false;
        lock (_sync)
        {
            if (_scopeStack.Count > 0)
            {
                var scope = _scopeStack.Peek();
                scope.AddOrUpdateChange(target, propertyName, previousValue, newValue);
                return;
            }

            var step = new PropertyChangeStep(target, propertyName, previousValue, newValue);
            var action = new UndoAction(BuildDescription(step), [step], DateTime.UtcNow);
            _undoStack.Push(action);
            _redoStack.Clear();
            addedImmediate = true;
        }

        if (addedImmediate)
            RaiseHistoryChanged();
    }

    private static void ApplyUndo(UndoAction action)
    {
        using var _ = new RecordingSuppressionScope();
        action.Undo();
    }

    private static void ApplyRedo(UndoAction action)
    {
        using var _ = new RecordingSuppressionScope();
        action.Redo();
    }

    private static void FlushPendingScopes()
    {
        ChangeScope[] scopes;
        lock (_sync)
            scopes = _scopeStack.ToArray();

        foreach (var scope in scopes)
            scope.Dispose();
    }

    private static void CompleteScope(ChangeScope scope)
    {
        UndoAction? committedAction = null;
        lock (_sync)
        {
            if (scope.Completed)
                return;

            if (_scopeStack.Count == 0 || !ReferenceEquals(_scopeStack.Peek(), scope))
                throw new InvalidOperationException("Change scopes must be disposed in LIFO order.");

            _scopeStack.Pop();
            scope.Completed = true;

            if (scope.IsCanceled || scope.Changes.Count == 0)
                return;

            var steps = scope.Changes.Select(c => c.Clone()).ToList();
            committedAction = new UndoAction(scope.Description, steps, scope.TimestampUtc);
            _undoStack.Push(committedAction);
            _redoStack.Clear();
        }

        if (committedAction is not null)
            RaiseHistoryChanged();
    }

    private static IReadOnlyList<UndoEntry> Snapshot(Stack<UndoAction> stack)
    {
        var entries = stack.ToArray();
        var list = new List<UndoEntry>(entries.Length);
        foreach (var action in entries)
            list.Add(action.ToEntry());
        return list;
    }

    private static string BuildDescription(PropertyChangeStep step)
    {
        string target = GetDisplayName(step.Target);
        return $"{target}: {step.PropertyName}";
    }

    private static string GetDisplayName(XRBase target)
    {
        if (target is XRObjectBase xrObject && !string.IsNullOrWhiteSpace(xrObject.Name))
            return xrObject.Name!;

        var type = target.GetType();
        var nameProperty = type.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (nameProperty?.PropertyType == typeof(string) && nameProperty.GetIndexParameters().Length == 0)
        {
            if (nameProperty.GetValue(target) is string value && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return type.Name;
    }

    private static void OnTrackedObjectPropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
    {
        if (sender is not XRBase target)
            return;

        if (RecordingAllowed)
            RecordPropertyChange(target, args.PropertyName ?? string.Empty, args.PreviousValue, args.NewValue);

        if (target is SceneNode node && string.Equals(args.PropertyName, nameof(SceneNode.Transform), StringComparison.Ordinal))
        {
            lock (_sync)
            {
                if (_trackedObjects.TryGetValue(node, out var context) && context.SceneNodeContext is not null)
                    context.SceneNodeContext.AttachTransform(node.Transform);
            }
        }
    }

    private static void RaiseHistoryChanged()
    {
        HistoryChanged?.Invoke();
    }

    private static bool RecordingAllowed => Volatile.Read(ref _suppressRecordingCount) == 0 && UserInteractionActive;

    private static bool UserInteractionActive => Volatile.Read(ref _activeUserInteractionCount) > 0;

    private readonly struct RecordingSuppressionScope : IDisposable
    {
        public RecordingSuppressionScope() => Interlocked.Increment(ref _suppressRecordingCount);
        public void Dispose() => Interlocked.Decrement(ref _suppressRecordingCount);
    }

    private static void ScheduleTransformRefresh(TransformBase? transform)
    {
        if (transform is null)
            return;

        EnsureTransformRefreshHooked();
        _pendingTransformRefresh.Enqueue(transform);
    }

    private static void EnsureTransformRefreshHooked()
    {
        if (Interlocked.Exchange(ref _transformRefreshHooked, 1) == 0)
            Engine.Time.Timer.UpdateFrame += ProcessPendingTransformRefresh;
    }

    private static void ProcessPendingTransformRefresh()
    {
        if (_pendingTransformRefresh.IsEmpty)
            return;

        var processed = new HashSet<TransformBase>(ReferenceEqualityComparer.Instance);

        while (_pendingTransformRefresh.TryDequeue(out var transform))
        {
            if (transform is null || !processed.Add(transform))
                continue;

            try
            {
                if (transform.World is { } world && !transform.ForceManualRecalc)
                    world.AddDirtyTransform(transform);
                else
                    transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Undo failed while queuing transform '{transform.Name ?? transform.GetType().Name}' for recompute: {ex.Message}");
            }
        }
    }

    private sealed class UserInteractionScope(Undo.UserInteractionContext context) : IDisposable
    {
        private UserInteractionContext? _context = context;

        public void Dispose()
        {
            var context = Interlocked.Exchange(ref _context, null);
            if (context is null)
                return;

            if (context.Depth <= 0)
            {
                if (ReferenceEquals(_userInteractionContext.Value, context))
                    _userInteractionContext.Value = null;
                return;
            }

            context.Depth--;

            int remaining = Interlocked.Decrement(ref _activeUserInteractionCount);
            if (remaining < 0)
                Interlocked.Exchange(ref _activeUserInteractionCount, 0);

            if (context.Depth == 0 && ReferenceEquals(_userInteractionContext.Value, context))
                _userInteractionContext.Value = null;
        }
    }

    private sealed class TrackedObject(XRBase instance)
    {
        private Action? _dispose;

        public XRBase Instance { get; } = instance;
        public SceneNodeContext? SceneNodeContext { get; set; }

        public void AddDisposeAction(Action dispose)
        {
            if (_dispose is null)
                _dispose = dispose;
            else
                _dispose += dispose;
        }

        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
            SceneNodeContext?.Dispose();
            SceneNodeContext = null;
        }
    }

    private sealed class SceneNodeContext : IDisposable
    {
        private readonly SceneNode _node;
        private readonly Action<(SceneNode node, XRComponent comp)> _componentAdded;
        private readonly Action<(SceneNode node, XRComponent comp)> _componentRemoved;
        private readonly EventList<TransformBase>.SingleHandler _childAdded;
        private readonly EventList<TransformBase>.SingleHandler _childRemoved;
        private TransformBase? _transform;

        public SceneNodeContext(SceneNode node)
        {
            _node = node;
            _componentAdded = data =>
            {
                if (data.comp is not null)
                    Track(data.comp);
            };
            _componentRemoved = data =>
            {
                if (data.comp is not null)
                    Untrack(data.comp);
            };
            _childAdded = transform =>
            {
                if (transform?.SceneNode is SceneNode childNode)
                    Track(childNode);
            };
            _childRemoved = _ => { };

            _node.ComponentAdded += _componentAdded;
            _node.ComponentRemoved += _componentRemoved;

            foreach (var component in _node.Components)
                Track(component);

            AttachTransform(_node.Transform);
        }

        public void AttachTransform(TransformBase? transform)
        {
            if (ReferenceEquals(_transform, transform))
                return;

            if (_transform is not null)
            {
                var children = _transform.Children;
                children.PostAnythingAdded -= _childAdded;
                children.PostAnythingRemoved -= _childRemoved;
                Untrack(_transform);
            }

            _transform = transform;

            if (_transform is null)
                return;

            Track(_transform);

            var childList = _transform.Children;
            foreach (var child in childList)
            {
                if (child?.SceneNode is SceneNode childNode)
                    Track(childNode);
            }

            childList.PostAnythingAdded += _childAdded;
            childList.PostAnythingRemoved += _childRemoved;
        }

        public void Dispose()
        {
            _node.ComponentAdded -= _componentAdded;
            _node.ComponentRemoved -= _componentRemoved;

            if (_transform is not null)
            {
                var children = _transform.Children;
                children.PostAnythingAdded -= _childAdded;
                children.PostAnythingRemoved -= _childRemoved;
                Untrack(_transform);
                _transform = null;
            }
        }
    }

    private sealed class UserInteractionContext
    {
        public int Depth;
    }

    public sealed record UndoEntry(string Description, DateTime TimestampUtc, IReadOnlyList<UndoChangeInfo> Changes);

    public sealed record UndoChangeInfo(WeakReference<XRBase> Target, string TargetDisplayName, Type TargetType, string PropertyName, object? PreviousValue, object? NewValue);

    public sealed class ChangeScope : IDisposable
    {
        internal ChangeScope(string description)
        {
            Description = description;
            TimestampUtc = DateTime.UtcNow;
        }

        internal string Description { get; }
        internal DateTime TimestampUtc { get; private set; }
        internal List<PropertyChangeStep> Changes { get; } = [];
        internal bool IsCanceled { get; private set; }
        internal bool Completed { get; set; }

        public void Cancel() => IsCanceled = true;

        internal void AddOrUpdateChange(XRBase target, string propertyName, object? previousValue, object? newValue)
        {
            if (Equals(previousValue, newValue))
                return;

            var existing = Changes.FirstOrDefault(c => ReferenceEquals(c.Target, target) && string.Equals(c.PropertyName, propertyName, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (Equals(existing.OriginalValue, newValue))
                    Changes.Remove(existing);
                else
                    existing.UpdateNewValue(newValue);
            }
            else
                Changes.Add(new PropertyChangeStep(target, propertyName, previousValue, newValue));

            TimestampUtc = DateTime.UtcNow;
        }

        public void Dispose() => CompleteScope(this);
    }

    internal sealed class PropertyChangeStep(XRBase target, string propertyName, object? previousValue, object? newValue)
    {
        private PropertyInfo? _cachedProperty;
        private FieldInfo? _cachedField;

        public XRBase Target { get; } = target;
        public string PropertyName { get; } = propertyName;
        public object? OriginalValue { get; } = previousValue;
        public object? CurrentValue { get; private set; } = newValue;

        public void UpdateNewValue(object? value) => CurrentValue = value;

        public void ApplyOld() => Apply(OriginalValue);
        public void ApplyNew() => Apply(CurrentValue);

        public PropertyChangeStep Clone() => new(Target, PropertyName, OriginalValue, CurrentValue);

        public UndoChangeInfo ToInfo()
            => new(new WeakReference<XRBase>(Target), GetDisplayName(Target), Target.GetType(), PropertyName, OriginalValue, CurrentValue);

        private void Apply(object? value)
        {
            if (!TrySetValue(value))
            {
                Debug.LogWarning($"Undo failed to set '{PropertyName}' on {Target.GetType().Name}.");
                return;
            }

#if DEBUG || EDITOR
            if (Target is TransformBase transform && transform.World is null)
            {
                string name = transform.SceneNode?.Name ?? transform.Name ?? transform.GetType().Name;
                Debug.LogWarning($"Undo applied '{PropertyName}' on transform '{name}', but World is null.");
            }
#endif

            PostApplyChange(Target, PropertyName);
        }

        private bool TrySetValue(object? value)
        {
            _cachedProperty ??= ResolveProperty(Target.GetType(), PropertyName);
            if (_cachedProperty is not null)
            {
                try
                {
                    _cachedProperty.SetValue(Target, value);
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Undo failed while setting property '{PropertyName}': {ex.Message}");
                    return false;
                }
            }

            _cachedField ??= ResolveField(Target.GetType(), PropertyName);
            if (_cachedField is not null)
            {
                try
                {
                    _cachedField.SetValue(Target, value);
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Undo failed while setting field '{PropertyName}': {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        private static void PostApplyChange(XRBase target, string propertyName)
        {
            switch (target)
            {
                case TransformBase transform:
                    ScheduleTransformRefresh(transform);
                    break;
                case SceneNode node when string.Equals(propertyName, nameof(SceneNode.Transform), StringComparison.Ordinal):
                    if (node.Transform is TransformBase nodeTransform)
                        ScheduleTransformRefresh(nodeTransform);
                    break;
            }
        }

        private static PropertyInfo? ResolveProperty(Type type, string propertyName)
        {
            while (type is not null)
            {
                var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop is not null && prop.GetIndexParameters().Length == 0)
                    return prop;
                type = type.BaseType!;
            }
            return null;
        }

        private static FieldInfo? ResolveField(Type type, string fieldName)
        {
            while (type is not null)
            {
                var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field is not null)
                    return field;
                type = type.BaseType!;
            }
            return null;
        }
    }

    private sealed class UndoAction(string description, List<Undo.PropertyChangeStep> changes, DateTime timestampUtc)
    {
        private readonly List<PropertyChangeStep> _changes = changes;

        public string Description { get; } = description;
        public DateTime TimestampUtc { get; } = timestampUtc;

        public void Undo()
        {
            for (int i = _changes.Count - 1; i >= 0; i--)
                _changes[i].ApplyOld();
        }

        public void Redo()
        {
            foreach (var change in _changes)
                change.ApplyNew();
        }

        public UndoEntry ToEntry()
            => new(Description, TimestampUtc, _changes.Select(c => c.ToInfo()).ToList());
    }

    private readonly struct ShortcutHandlers(Action onUndo, Action onRedo)
    {
        public Action OnUndo { get; } = onUndo;
        public Action OnRedo { get; } = onRedo;
    }

    private enum ShortcutKind
    {
        Undo,
        Redo
    }
}
