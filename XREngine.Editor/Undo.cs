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
/// Provides a comprehensive undo/redo system for the XREngine editor.
/// <para>
/// This static class captures property edits on <see cref="XRBase"/> objects and maintains
/// a history of changes that can be undone and redone. It supports:
/// <list type="bullet">
///   <item><description>Automatic property change tracking via <see cref="Track(XRBase?)"/></description></item>
///   <item><description>Grouped changes via <see cref="BeginChange(string)"/> scopes</description></item>
///   <item><description>User interaction contexts for selective recording</description></item>
///   <item><description>Keyboard shortcuts (Ctrl+Z for undo, Ctrl+Y or Ctrl+Shift+Z for redo)</description></item>
///   <item><description>Automatic transform matrix recalculation after undo/redo</description></item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// The undo system works by subscribing to <see cref="XRBase.PropertyChanged"/> events on tracked objects.
/// When a property changes, the previous and new values are recorded. Changes can be grouped into
/// <see cref="ChangeScope"/> instances to create atomic undo operations.
/// </para>
/// <para>
/// Thread safety is ensured through locking on <see cref="_sync"/> for shared state access.
/// The system uses <see cref="AsyncLocal{T}"/> to track user interaction context per async flow.
/// </para>
/// </remarks>
public static class Undo
{
    /// <summary>
    /// Synchronization object for thread-safe access to shared state.
    /// </summary>
    private static readonly object _sync = new();

    /// <summary>
    /// Maps tracked <see cref="XRBase"/> instances to their tracking context.
    /// Uses reference equality to identify unique object instances.
    /// </summary>
    private static readonly Dictionary<XRBase, TrackedObject> _trackedObjects = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Stack of active change scopes. Changes are accumulated in the topmost scope
    /// until it is disposed, at which point they are committed to the undo stack.
    /// </summary>
    private static readonly Stack<ChangeScope> _scopeStack = new();

    /// <summary>
    /// Stack of undo actions available for undoing. Actions are pushed when changes
    /// are committed and popped when <see cref="TryUndo"/> is called.
    /// </summary>
    private static readonly Stack<UndoAction> _undoStack = new();

    /// <summary>
    /// Stack of redo actions available for redoing. Actions are pushed from the undo
    /// stack when undone and cleared when new changes are made.
    /// </summary>
    private static readonly Stack<UndoAction> _redoStack = new();

    /// <summary>
    /// Maps input interfaces to their registered keyboard shortcut handlers.
    /// Used to properly unregister handlers when input interfaces are destroyed.
    /// </summary>
    private static readonly Dictionary<LocalInputInterface, ShortcutHandlers> _shortcutHandlers = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Async-local storage for user interaction context. Each async execution flow
    /// maintains its own context to properly track nested interaction scopes.
    /// </summary>
    private static readonly AsyncLocal<UserInteractionContext?> _userInteractionContext = new();

    /// <summary>
    /// Global count of active user interaction scopes across all threads.
    /// Property changes are only recorded when this is greater than zero.
    /// </summary>
    private static int _activeUserInteractionCount;

    /// <summary>
    /// Counter for recording suppression scopes. When greater than zero,
    /// property changes are not recorded (used during undo/redo application).
    /// </summary>
    private static int _suppressRecordingCount;

    /// <summary>
    /// Indicates whether <see cref="Initialize"/> has been called.
    /// </summary>
    private static bool _initialized;

    /// <summary>
    /// Queue of transforms pending matrix recalculation after undo/redo operations.
    /// Uses a concurrent queue for thread-safe enqueueing from any thread.
    /// </summary>
    private static readonly ConcurrentQueue<TransformBase> _pendingTransformRefresh = new();

    /// <summary>
    /// Flag indicating whether the transform refresh hook has been registered.
    /// Uses interlocked operations to ensure single registration.
    /// </summary>
    private static int _transformRefreshHooked;

    /// <summary>
    /// Event raised when the undo/redo history changes (action added, undone, redone, or cleared).
    /// UI components can subscribe to update their state accordingly.
    /// </summary>
    public static event Action? HistoryChanged;

    /// <summary>
    /// Gets a value indicating whether there are any actions available to undo.
    /// </summary>
    /// <value><c>true</c> if the undo stack is not empty; otherwise, <c>false</c>.</value>
    public static bool CanUndo
    {
        get
        {
            lock (_sync)
                return _undoStack.Count > 0;
        }
    }

    /// <summary>
    /// Gets a value indicating whether there are any actions available to redo.
    /// </summary>
    /// <value><c>true</c> if the redo stack is not empty; otherwise, <c>false</c>.</value>
    public static bool CanRedo
    {
        get
        {
            lock (_sync)
                return _redoStack.Count > 0;
        }
    }

    /// <summary>
    /// Gets a snapshot of all pending undo entries in order from most recent to oldest.
    /// </summary>
    /// <value>A read-only list of <see cref="UndoEntry"/> records describing each undoable action.</value>
    /// <remarks>
    /// This creates a copy of the current undo stack for safe iteration.
    /// The list is not updated as the undo history changes.
    /// </remarks>
    public static IReadOnlyList<UndoEntry> PendingUndo
    {
        get
        {
            lock (_sync)
                return Snapshot(_undoStack);
        }
    }

    /// <summary>
    /// Gets a snapshot of all pending redo entries in order from most recent to oldest.
    /// </summary>
    /// <value>A read-only list of <see cref="UndoEntry"/> records describing each redoable action.</value>
    /// <remarks>
    /// This creates a copy of the current redo stack for safe iteration.
    /// The list is not updated as the redo history changes.
    /// </remarks>
    public static IReadOnlyList<UndoEntry> PendingRedo
    {
        get
        {
            lock (_sync)
                return Snapshot(_redoStack);
        }
    }

    /// <summary>
    /// Initializes the undo system by registering global input handlers for keyboard shortcuts.
    /// </summary>
    /// <remarks>
    /// This method is idempotent - calling it multiple times has no additional effect.
    /// It registers Ctrl+Z for undo and Ctrl+Y (or Ctrl+Shift+Z) for redo shortcuts.
    /// Also ensures the transform refresh hook is registered for processing pending transforms.
    /// </remarks>
    public static void Initialize()
    {
        lock (_sync)
        {
            if (_initialized)
                return;

            // Register to receive notifications when new input interfaces are created
            LocalInputInterface.GlobalRegisters.Add(RegisterGlobalInput);
            EnsureTransformRefreshHooked();
            _initialized = true;
        }
    }

    /// <summary>
    /// Begins a new change scope that groups multiple property changes into a single undoable action.
    /// </summary>
    /// <param name="description">A human-readable description of the change (e.g., "Move Object", "Change Color").</param>
    /// <returns>
    /// A <see cref="ChangeScope"/> that should be disposed when the grouped changes are complete.
    /// Use a <c>using</c> statement to ensure proper cleanup.
    /// </returns>
    /// <remarks>
    /// <para>
    /// While a change scope is active, all property changes on tracked objects are accumulated
    /// in that scope rather than being immediately committed to the undo stack. When the scope
    /// is disposed, all accumulated changes are committed as a single undoable action.
    /// </para>
    /// <para>
    /// Change scopes can be nested. Changes are always added to the innermost active scope.
    /// Scopes must be disposed in LIFO (last-in-first-out) order.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using (Undo.BeginChange("Move Selected Objects"))
    /// {
    ///     foreach (var obj in selectedObjects)
    ///         obj.Position = newPosition;
    /// }
    /// // All position changes are now a single undoable action
    /// </code>
    /// </example>
    public static ChangeScope BeginChange(string description)
    {
        var scope = new ChangeScope(string.IsNullOrWhiteSpace(description) ? "Change" : description.Trim());
        lock (_sync)
            _scopeStack.Push(scope);
        return scope;
    }

    /// <summary>
    /// Begins a user interaction context that enables property change recording.
    /// </summary>
    /// <returns>
    /// An <see cref="IDisposable"/> that should be disposed when the user interaction ends.
    /// Use a <c>using</c> statement to ensure proper cleanup.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Property changes are only recorded when at least one user interaction context is active.
    /// This prevents programmatic changes (like loading a scene) from being recorded as undoable actions.
    /// </para>
    /// <para>
    /// User interaction contexts can be nested. The recording remains active until all contexts are disposed.
    /// Each async execution flow maintains its own context via <see cref="AsyncLocal{T}"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using (Undo.BeginUserInteraction())
    /// {
    ///     // Property changes made here will be recorded for undo
    ///     selectedObject.Name = userInput;
    /// }
    /// </code>
    /// </example>
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

    /// <summary>
    /// Attempts to undo the most recent action from the undo stack.
    /// </summary>
    /// <returns>
    /// <c>true</c> if an action was successfully undone; <c>false</c> if the undo stack was empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method first flushes any pending change scopes, then pops the most recent action
    /// from the undo stack, applies the old values, and pushes the action to the redo stack.
    /// </para>
    /// <para>
    /// Property change recording is suppressed during the undo operation to prevent the
    /// undo itself from being recorded as a new change.
    /// </para>
    /// <para>
    /// After the undo is applied, any affected transforms have their matrices recalculated.
    /// </para>
    /// </remarks>
    public static bool TryUndo()
    {
        // Ensure any open change scopes are committed before undoing
        FlushPendingScopes();

        UndoAction? action;
        lock (_sync)
        {
            if (_undoStack.Count == 0)
                return false;

            // Move action from undo stack to redo stack
            action = _undoStack.Pop();
            _redoStack.Push(action);
        }

        // Apply the undo operation with recording suppressed
        ApplyUndo(action);
        ProcessPendingTransformRefresh();
        RaiseHistoryChanged();
        return true;
    }

    /// <summary>
    /// Attempts to redo the most recently undone action from the redo stack.
    /// </summary>
    /// <returns>
    /// <c>true</c> if an action was successfully redone; <c>false</c> if the redo stack was empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method first flushes any pending change scopes, then pops the most recent action
    /// from the redo stack, applies the new values, and pushes the action back to the undo stack.
    /// </para>
    /// <para>
    /// Property change recording is suppressed during the redo operation to prevent the
    /// redo itself from being recorded as a new change.
    /// </para>
    /// <para>
    /// After the redo is applied, any affected transforms have their matrices recalculated.
    /// </para>
    /// </remarks>
    public static bool TryRedo()
    {
        // Ensure any open change scopes are committed before redoing
        FlushPendingScopes();

        UndoAction? action;
        lock (_sync)
        {
            if (_redoStack.Count == 0)
                return false;

            // Move action from redo stack back to undo stack
            action = _redoStack.Pop();
            _undoStack.Push(action);
        }

        // Apply the redo operation with recording suppressed
        ApplyRedo(action);
        ProcessPendingTransformRefresh();
        RaiseHistoryChanged();
        return true;
    }

    /// <summary>
    /// Clears all undo and redo history.
    /// </summary>
    /// <remarks>
    /// This removes all recorded actions from both the undo and redo stacks.
    /// Typically called when loading a new scene or when the history is no longer relevant.
    /// Raises the <see cref="HistoryChanged"/> event after clearing.
    /// </remarks>
    public static void ClearHistory()
    {
        lock (_sync)
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        RaiseHistoryChanged();
    }

    /// <summary>
    /// Begins tracking property changes on the specified <see cref="XRBase"/> instance.
    /// </summary>
    /// <param name="instance">The object to track. If <c>null</c>, no action is taken.</param>
    /// <remarks>
    /// <para>
    /// Once tracked, any property changes on the object (via <see cref="XRBase.PropertyChanged"/>)
    /// will be recorded for undo/redo when a user interaction context is active.
    /// </para>
    /// <para>
    /// Tracking is idempotent - calling this multiple times with the same instance has no additional effect.
    /// </para>
    /// <para>
    /// Special handling is configured for:
    /// <list type="bullet">
    ///   <item><description><see cref="SceneNode"/>: Tracks components and child transforms</description></item>
    ///   <item><description><see cref="TransformBase"/>: Schedules matrix recalculation on changes</description></item>
    ///   <item><description><see cref="XRComponent"/>: Automatically untracks when destroyed</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <seealso cref="Untrack(XRBase?)"/>
    public static void Track(XRBase? instance)
    {
        if (instance is null)
            return;

        lock (_sync)
        {
            // Skip if already being tracked
            if (_trackedObjects.ContainsKey(instance))
                return;

            // Create tracking context and subscribe to property changes
            var context = new TrackedObject(instance);
            instance.PropertyChanged += OnTrackedObjectPropertyChanged;
            context.AddDisposeAction(() => instance.PropertyChanged -= OnTrackedObjectPropertyChanged);
            _trackedObjects.Add(instance, context);

            // Configure type-specific tracking behavior
            ConfigureSpecializedTracking(instance, context);
        }
    }

    /// <summary>
    /// Stops tracking property changes on the specified <see cref="XRBase"/> instance.
    /// </summary>
    /// <param name="instance">The object to stop tracking. If <c>null</c>, no action is taken.</param>
    /// <remarks>
    /// This unsubscribes from property change events and cleans up any specialized tracking
    /// context associated with the object. Safe to call even if the object is not being tracked.
    /// </remarks>
    /// <seealso cref="Track(XRBase?)"/>
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

        // Dispose outside lock to avoid potential deadlocks
        context.Dispose();
    }

    /// <summary>
    /// Tracks an entire <see cref="XRWorld"/> and all its contained objects for undo/redo.
    /// </summary>
    /// <param name="world">The world to track. If <c>null</c>, no action is taken.</param>
    /// <remarks>
    /// This recursively tracks:
    /// <list type="bullet">
    ///   <item><description>The world itself</description></item>
    ///   <item><description>The world's settings</description></item>
    ///   <item><description>The default game mode (if it derives from <see cref="XRBase"/>)</description></item>
    ///   <item><description>All scenes and their contents via <see cref="TrackScene(XRScene?)"/></description></item>
    /// </list>
    /// </remarks>
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

    /// <summary>
    /// Tracks an entire <see cref="XRScene"/> and all its root nodes for undo/redo.
    /// </summary>
    /// <param name="scene">The scene to track. If <c>null</c>, no action is taken.</param>
    /// <remarks>
    /// This tracks the scene itself and all root nodes via <see cref="TrackSceneNode(SceneNode?)"/>.
    /// Child nodes are tracked automatically through the <see cref="SceneNodeContext"/>.
    /// </remarks>
    public static void TrackScene(XRScene? scene)
    {
        if (scene is null)
            return;

        Track(scene);
        foreach (var node in scene.RootNodes)
            TrackSceneNode(node);
    }

    /// <summary>
    /// Tracks a <see cref="SceneNode"/> for undo/redo.
    /// </summary>
    /// <param name="node">The scene node to track. If <c>null</c>, no action is taken.</param>
    /// <remarks>
    /// This sets up a <see cref="SceneNodeContext"/> that automatically tracks:
    /// <list type="bullet">
    ///   <item><description>The node itself</description></item>
    ///   <item><description>All components attached to the node</description></item>
    ///   <item><description>The node's transform</description></item>
    ///   <item><description>Child nodes (tracked when added to the transform's children)</description></item>
    /// </list>
    /// </remarks>
    public static void TrackSceneNode(SceneNode? node)
    {
        if (node is null)
            return;

        Track(node);
    }

    /// <summary>
    /// Helper method to track an object if it derives from <see cref="XRBase"/>.
    /// </summary>
    /// <param name="instance">The object to potentially track.</param>
    private static void TrackIfXRBase(object? instance)
    {
        if (instance is XRBase xr)
            Track(xr);
    }

    /// <summary>
    /// Registers keyboard shortcuts (Ctrl+Z, Ctrl+Y) for undo/redo with a local input interface.
    /// </summary>
    /// <param name="inputInterface">The input interface that was registered or unregistered.</param>
    /// <remarks>
    /// This method is called automatically when input interfaces are registered/unregistered globally.
    /// It handles both registration (when <see cref="InputInterface.Unregister"/> is <c>false</c>)
    /// and unregistration (when <c>true</c>) of keyboard shortcuts.
    /// </remarks>
    private static void RegisterGlobalInput(InputInterface inputInterface)
    {
        if (inputInterface is not LocalInputInterface local)
            return;

        lock (_sync)
        {
            // Handle unregistration
            if (inputInterface.Unregister)
            {
                if (_shortcutHandlers.TryGetValue(local, out ShortcutHandlers handlers))
                {
                    // Remove the keyboard event handlers
                    local.RegisterKeyEvent(EKey.Z, EButtonInputType.Pressed, handlers.OnUndo);
                    local.RegisterKeyEvent(EKey.Y, EButtonInputType.Pressed, handlers.OnRedo);
                    _shortcutHandlers.Remove(local);
                }
                return;
            }

            // Skip if already registered
            if (_shortcutHandlers.ContainsKey(local))
                return;

            // Create and register handlers for undo (Ctrl+Z) and redo (Ctrl+Y)
            Action undoHandler = () => HandleShortcut(local, ShortcutKind.Undo);
            Action redoHandler = () => HandleShortcut(local, ShortcutKind.Redo);
            _shortcutHandlers[local] = new ShortcutHandlers(undoHandler, redoHandler);

            local.RegisterKeyEvent(EKey.Z, EButtonInputType.Pressed, undoHandler);
            local.RegisterKeyEvent(EKey.Y, EButtonInputType.Pressed, redoHandler);
        }
    }

    /// <summary>
    /// Handles a keyboard shortcut press for undo or redo.
    /// </summary>
    /// <param name="input">The input interface that received the key press.</param>
    /// <param name="kind">The type of shortcut (Undo or Redo).</param>
    /// <remarks>
    /// <para>
    /// Shortcuts are only active when in edit mode (<see cref="EditorState.InEditMode"/>).
    /// </para>
    /// <para>
    /// For undo (Ctrl+Z): If Shift is also held, performs redo instead (Ctrl+Shift+Z).
    /// For redo (Ctrl+Y): Always performs redo.
    /// </para>
    /// </remarks>
    private static void HandleShortcut(LocalInputInterface input, ShortcutKind kind)
    {
        // Only handle shortcuts in edit mode
        if (!EditorState.InEditMode)
            return;

        // Require Ctrl to be held
        if (!IsCtrlDown(input))
            return;

        if (kind == ShortcutKind.Undo)
        {
            // Ctrl+Shift+Z = Redo (alternative shortcut)
            if (IsShiftDown(input))
            {
                TryRedo();
                return;
            }

            // Ctrl+Z = Undo
            TryUndo();
        }
        else
        {
            // Ctrl+Y = Redo
            TryRedo();
        }
    }

    /// <summary>
    /// Checks if either Control key is currently pressed or held.
    /// </summary>
    /// <param name="input">The input interface to check.</param>
    /// <returns><c>true</c> if Ctrl is down; otherwise, <c>false</c>.</returns>
    private static bool IsCtrlDown(LocalInputInterface input)
        => input.GetKeyState(EKey.ControlLeft, EButtonInputType.Held)
        || input.GetKeyState(EKey.ControlRight, EButtonInputType.Held)
        || input.GetKeyState(EKey.ControlLeft, EButtonInputType.Pressed)
        || input.GetKeyState(EKey.ControlRight, EButtonInputType.Pressed);

    /// <summary>
    /// Checks if either Shift key is currently pressed or held.
    /// </summary>
    /// <param name="input">The input interface to check.</param>
    /// <returns><c>true</c> if Shift is down; otherwise, <c>false</c>.</returns>
    private static bool IsShiftDown(LocalInputInterface input)
        => input.GetKeyState(EKey.ShiftLeft, EButtonInputType.Held)
        || input.GetKeyState(EKey.ShiftRight, EButtonInputType.Held)
        || input.GetKeyState(EKey.ShiftLeft, EButtonInputType.Pressed)
        || input.GetKeyState(EKey.ShiftRight, EButtonInputType.Pressed);

    /// <summary>
    /// Configures type-specific tracking behavior for special object types.
    /// </summary>
    /// <param name="instance">The tracked object instance.</param>
    /// <param name="context">The tracking context for the instance.</param>
    /// <remarks>
    /// Sets up specialized tracking for:
    /// <list type="bullet">
    ///   <item><description><see cref="SceneNode"/>: Creates a <see cref="SceneNodeContext"/> for component/child tracking</description></item>
    ///   <item><description><see cref="TransformBase"/>: Configures transform-specific hooks</description></item>
    ///   <item><description><see cref="XRComponent"/>: Configures auto-untrack on destruction</description></item>
    /// </list>
    /// </remarks>
    private static void ConfigureSpecializedTracking(XRBase instance, TrackedObject context)
    {
        switch (instance)
        {
            case SceneNode node:
                // Create context to track components and child transforms
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

    /// <summary>
    /// Configures transform-specific tracking behavior.
    /// </summary>
    /// <param name="transform">The transform to configure tracking for.</param>
    /// <param name="context">The tracking context for the transform.</param>
    /// <remarks>
    /// Currently a placeholder for future transform-specific hooks such as
    /// tracking parent changes or local-to-world matrix updates.
    /// </remarks>
    private static void ConfigureTransformTracking(TransformBase transform, TrackedObject context)
    {
        // Placeholder for transform-specific hooks.
        // Future: Could track parent changes, matrix updates, etc.
    }

    /// <summary>
    /// Configures component-specific tracking behavior, including auto-untrack on destruction.
    /// </summary>
    /// <param name="component">The component to configure tracking for.</param>
    /// <param name="context">The tracking context for the component.</param>
    /// <remarks>
    /// Subscribes to the component's <see cref="XRObjectBase.Destroyed"/> event to automatically
    /// untrack the component when it is destroyed, preventing memory leaks and invalid references.
    /// </remarks>
    private static void ConfigureComponentTracking(XRComponent component, TrackedObject context)
    {
        // Auto-untrack when component is destroyed
        component.Destroyed += ComponentDestroyed;
        context.AddDisposeAction(() => component.Destroyed -= ComponentDestroyed);

        static void ComponentDestroyed(XRObjectBase obj)
        {
            if (obj is XRComponent comp)
                Untrack(comp);
        }
    }

    /// <summary>
    /// Records a property change for undo/redo tracking.
    /// </summary>
    /// <param name="target">The object whose property changed.</param>
    /// <param name="propertyName">The name of the changed property.</param>
    /// <param name="previousValue">The value before the change.</param>
    /// <param name="newValue">The value after the change.</param>
    /// <remarks>
    /// <para>
    /// If a <see cref="ChangeScope"/> is active, the change is added to that scope.
    /// Otherwise, it is immediately committed as a new undo action.
    /// </para>
    /// <para>
    /// Changes where the previous and new values are equal are ignored.
    /// Adding a new action clears the redo stack.
    /// </para>
    /// </remarks>
    private static void RecordPropertyChange(XRBase target, string propertyName, object? previousValue, object? newValue)
    {
        // Ignore empty property names
        if (string.IsNullOrEmpty(propertyName))
            return;

        // Ignore no-op changes
        if (Equals(previousValue, newValue))
            return;

        bool addedImmediate = false;
        lock (_sync)
        {
            // If there's an active scope, add to it instead of creating immediate action
            if (_scopeStack.Count > 0)
            {
                var scope = _scopeStack.Peek();
                scope.AddOrUpdateChange(target, propertyName, previousValue, newValue);
                return;
            }

            // No scope active - create immediate undo action
            var step = new PropertyChangeStep(target, propertyName, previousValue, newValue);
            var action = new UndoAction(BuildDescription(step), [step], DateTime.UtcNow);
            _undoStack.Push(action);
            _redoStack.Clear(); // Clear redo stack when new changes are made
            addedImmediate = true;
        }

        if (addedImmediate)
            RaiseHistoryChanged();
    }

    /// <summary>
    /// Applies an undo action by reverting to original values with recording suppressed.
    /// </summary>
    /// <param name="action">The action to undo.</param>
    private static void ApplyUndo(UndoAction action)
    {
        // Suppress recording so the undo operation itself isn't recorded
        using var _ = new RecordingSuppressionScope();
        action.Undo();
    }

    /// <summary>
    /// Applies a redo action by restoring new values with recording suppressed.
    /// </summary>
    /// <param name="action">The action to redo.</param>
    private static void ApplyRedo(UndoAction action)
    {
        // Suppress recording so the redo operation itself isn't recorded
        using var _ = new RecordingSuppressionScope();
        action.Redo();
    }

    /// <summary>
    /// Disposes all pending change scopes, committing their accumulated changes.
    /// </summary>
    /// <remarks>
    /// Called before undo/redo operations to ensure all in-progress changes are committed.
    /// Scopes are disposed in order (though they commit in LIFO order internally).
    /// </remarks>
    private static void FlushPendingScopes()
    {
        ChangeScope[] scopes;
        lock (_sync)
            scopes = _scopeStack.ToArray();

        foreach (var scope in scopes)
            scope.Dispose();
    }

    /// <summary>
    /// Completes a change scope, committing its accumulated changes to the undo stack.
    /// </summary>
    /// <param name="scope">The scope to complete.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if scopes are not disposed in LIFO order (the scope being completed must be on top).
    /// </exception>
    /// <remarks>
    /// <para>
    /// If the scope was canceled or has no changes, nothing is committed.
    /// Otherwise, all changes are cloned and committed as a single <see cref="UndoAction"/>.
    /// </para>
    /// <para>
    /// Committing a new action clears the redo stack.
    /// </para>
    /// </remarks>
    private static void CompleteScope(ChangeScope scope)
    {
        UndoAction? committedAction = null;
        lock (_sync)
        {
            // Prevent double-completion
            if (scope.Completed)
                return;

            // Verify LIFO order
            if (_scopeStack.Count == 0 || !ReferenceEquals(_scopeStack.Peek(), scope))
                throw new InvalidOperationException("Change scopes must be disposed in LIFO order.");

            _scopeStack.Pop();
            scope.Completed = true;

            // Don't commit if canceled or empty
            if (scope.IsCanceled || scope.Changes.Count == 0)
                return;

            // Clone changes to prevent external modification
            var steps = scope.Changes.Select(c => c.Clone()).ToList();
            committedAction = new UndoAction(scope.Description, steps, scope.TimestampUtc);
            _undoStack.Push(committedAction);
            _redoStack.Clear();
        }

        if (committedAction is not null)
            RaiseHistoryChanged();
    }

    /// <summary>
    /// Creates a snapshot of an undo/redo stack as a list of <see cref="UndoEntry"/> records.
    /// </summary>
    /// <param name="stack">The stack to snapshot.</param>
    /// <returns>A read-only list of entry records for display purposes.</returns>
    private static IReadOnlyList<UndoEntry> Snapshot(Stack<UndoAction> stack)
    {
        var entries = stack.ToArray();
        var list = new List<UndoEntry>(entries.Length);
        foreach (var action in entries)
            list.Add(action.ToEntry());
        return list;
    }

    /// <summary>
    /// Builds a human-readable description for a single property change.
    /// </summary>
    /// <param name="step">The property change step.</param>
    /// <returns>A description in the format "ObjectName: PropertyName".</returns>
    private static string BuildDescription(PropertyChangeStep step)
    {
        string target = GetDisplayName(step.Target);
        return $"{target}: {step.PropertyName}";
    }

    /// <summary>
    /// Gets a display-friendly name for an <see cref="XRBase"/> object.
    /// </summary>
    /// <param name="target">The object to get a name for.</param>
    /// <returns>
    /// The object's <c>Name</c> property if available and non-empty;
    /// otherwise, the type name.
    /// </returns>
    private static string GetDisplayName(XRBase target)
    {
        // Try XRObjectBase.Name first
        if (target is XRObjectBase xrObject && !string.IsNullOrWhiteSpace(xrObject.Name))
            return xrObject.Name!;

        // Fall back to reflection to find a Name property
        var type = target.GetType();
        var nameProperty = type.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (nameProperty?.PropertyType == typeof(string) && nameProperty.GetIndexParameters().Length == 0)
        {
            if (nameProperty.GetValue(target) is string value && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        // Fall back to type name
        return type.Name;
    }

    /// <summary>
    /// Handles property change events from tracked objects.
    /// </summary>
    /// <param name="sender">The object that raised the event.</param>
    /// <param name="args">The event arguments containing property change details.</param>
    /// <remarks>
    /// <para>
    /// Records the property change if recording is currently allowed
    /// (user interaction active and not suppressed).
    /// </para>
    /// <para>
    /// Also handles special case where a <see cref="SceneNode"/>'s Transform property changes,
    /// updating the <see cref="SceneNodeContext"/> to track the new transform.
    /// </para>
    /// </remarks>
    private static void OnTrackedObjectPropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
    {
        if (sender is not XRBase target)
            return;

        // Record change if allowed
        if (RecordingAllowed)
            RecordPropertyChange(target, args.PropertyName ?? string.Empty, args.PreviousValue, args.NewValue);

        // Handle SceneNode.Transform property changes - need to re-attach transform tracking
        if (target is SceneNode node && string.Equals(args.PropertyName, nameof(SceneNode.Transform), StringComparison.Ordinal))
        {
            lock (_sync)
            {
                if (_trackedObjects.TryGetValue(node, out var context) && context.SceneNodeContext is not null)
                    context.SceneNodeContext.AttachTransform(node.Transform);
            }
        }
    }

    /// <summary>
    /// Invokes the <see cref="HistoryChanged"/> event on all subscribers.
    /// </summary>
    private static void RaiseHistoryChanged()
    {
        HistoryChanged?.Invoke();
    }

    /// <summary>
    /// Gets a value indicating whether property change recording is currently allowed.
    /// </summary>
    /// <value>
    /// <c>true</c> if recording is not suppressed and a user interaction is active; otherwise, <c>false</c>.
    /// </value>
    private static bool RecordingAllowed => Volatile.Read(ref _suppressRecordingCount) == 0 && UserInteractionActive;

    /// <summary>
    /// Gets a value indicating whether at least one user interaction context is active.
    /// </summary>
    /// <value><c>true</c> if user interaction count is greater than zero; otherwise, <c>false</c>.</value>
    private static bool UserInteractionActive => Volatile.Read(ref _activeUserInteractionCount) > 0;

    /// <summary>
    /// RAII-style scope that suppresses property change recording while active.
    /// Used during undo/redo operations to prevent recording the restoration of values.
    /// </summary>
    private readonly struct RecordingSuppressionScope : IDisposable
    {
        /// <summary>
        /// Initializes a new instance and increments the suppression counter.
        /// </summary>
        public RecordingSuppressionScope() => Interlocked.Increment(ref _suppressRecordingCount);

        /// <summary>
        /// Decrements the suppression counter, potentially re-enabling recording.
        /// </summary>
        public void Dispose() => Interlocked.Decrement(ref _suppressRecordingCount);
    }

    /// <summary>
    /// Schedules a transform to have its matrices recalculated on the next frame update.
    /// </summary>
    /// <param name="transform">The transform to refresh. If <c>null</c>, no action is taken.</param>
    /// <remarks>
    /// This batches transform updates to avoid redundant recalculations when multiple
    /// properties are changed in quick succession.
    /// </remarks>
    private static void ScheduleTransformRefresh(TransformBase? transform)
    {
        if (transform is null)
            return;

        EnsureTransformRefreshHooked();
        _pendingTransformRefresh.Enqueue(transform);
    }

    /// <summary>
    /// Ensures the frame update hook for processing pending transforms is registered.
    /// </summary>
    /// <remarks>
    /// Uses interlocked exchange to ensure the hook is only registered once,
    /// even when called from multiple threads simultaneously.
    /// </remarks>
    private static void EnsureTransformRefreshHooked()
    {
        if (Interlocked.Exchange(ref _transformRefreshHooked, 1) == 0)
            Engine.Time.Timer.UpdateFrame += ProcessPendingTransformRefresh;
    }

    /// <summary>
    /// Processes all pending transform refresh requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called each frame (via <see cref="Engine.Time.Timer.UpdateFrame"/>) and also
    /// immediately after undo/redo operations.
    /// </para>
    /// <para>
    /// Uses a hash set to deduplicate transforms, ensuring each is only processed once
    /// even if queued multiple times.
    /// </para>
    /// <para>
    /// For transforms with a world reference that don't force manual recalculation,
    /// the transform is marked dirty. Otherwise, matrices are recalculated immediately.
    /// </para>
    /// </remarks>
    private static void ProcessPendingTransformRefresh()
    {
        if (_pendingTransformRefresh.IsEmpty)
            return;

        // Track processed transforms to avoid duplicate work
        var processed = new HashSet<TransformBase>(ReferenceEqualityComparer.Instance);

        while (_pendingTransformRefresh.TryDequeue(out var transform))
        {
            // Skip null or already-processed transforms
            if (transform is null || !processed.Add(transform))
                continue;

            try
            {
                // If transform has a world and doesn't force manual recalc, mark dirty
                // Otherwise, force immediate recalculation
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

    #region Nested Types

    /// <summary>
    /// RAII-style scope that tracks a user interaction context.
    /// Disposes the context when the scope ends, decrementing the interaction count.
    /// </summary>
    /// <param name="context">The user interaction context being tracked.</param>
    private sealed class UserInteractionScope(Undo.UserInteractionContext context) : IDisposable
    {
        private UserInteractionContext? _context = context;

        /// <summary>
        /// Ends the user interaction scope, decrementing counters and cleaning up context if needed.
        /// </summary>
        public void Dispose()
        {
            // Exchange to null to prevent double-dispose
            var context = Interlocked.Exchange(ref _context, null);
            if (context is null)
                return;

            // Handle edge case where depth is already 0
            if (context.Depth <= 0)
            {
                if (ReferenceEquals(_userInteractionContext.Value, context))
                    _userInteractionContext.Value = null;
                return;
            }

            context.Depth--;

            // Decrement global counter, clamping to 0 if it goes negative
            int remaining = Interlocked.Decrement(ref _activeUserInteractionCount);
            if (remaining < 0)
                Interlocked.Exchange(ref _activeUserInteractionCount, 0);

            // Clear async-local context when depth reaches 0
            if (context.Depth == 0 && ReferenceEquals(_userInteractionContext.Value, context))
                _userInteractionContext.Value = null;
        }
    }

    /// <summary>
    /// Holds tracking state for a single <see cref="XRBase"/> instance.
    /// </summary>
    /// <param name="instance">The tracked object instance.</param>
    /// <remarks>
    /// Manages event subscriptions and specialized tracking contexts (like <see cref="SceneNodeContext"/>).
    /// Dispose actions are accumulated and invoked when the tracking is removed.
    /// </remarks>
    private sealed class TrackedObject(XRBase instance)
    {
        /// <summary>
        /// Accumulated dispose actions (event unsubscriptions, etc.).
        /// </summary>
        private Action? _dispose;

        /// <summary>
        /// Gets the tracked object instance.
        /// </summary>
        public XRBase Instance { get; } = instance;

        /// <summary>
        /// Gets or sets the scene node-specific tracking context, if this is a <see cref="SceneNode"/>.
        /// </summary>
        public SceneNodeContext? SceneNodeContext { get; set; }

        /// <summary>
        /// Adds an action to be invoked when this tracking context is disposed.
        /// </summary>
        /// <param name="dispose">The dispose action to add.</param>
        public void AddDisposeAction(Action dispose)
        {
            if (_dispose is null)
                _dispose = dispose;
            else
                _dispose += dispose;
        }

        /// <summary>
        /// Invokes all accumulated dispose actions and cleans up specialized contexts.
        /// </summary>
        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
            SceneNodeContext?.Dispose();
            SceneNodeContext = null;
        }
    }

    /// <summary>
    /// Specialized tracking context for <see cref="SceneNode"/> instances.
    /// Automatically tracks components and child transforms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When a SceneNode is tracked, this context subscribes to:
    /// <list type="bullet">
    ///   <item><description>ComponentAdded/ComponentRemoved events to track new components</description></item>
    ///   <item><description>Transform.Children.PostAnythingAdded/Removed to track child nodes</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// This ensures that dynamically added components and child nodes are automatically
    /// tracked for undo/redo without requiring manual registration.
    /// </para>
    /// </remarks>
    private sealed class SceneNodeContext : IDisposable
    {
        private readonly SceneNode _node;
        private readonly Action<(SceneNode node, XRComponent comp)> _componentAdded;
        private readonly Action<(SceneNode node, XRComponent comp)> _componentRemoved;
        private readonly EventList<TransformBase>.SingleHandler _childAdded;
        private readonly EventList<TransformBase>.SingleHandler _childRemoved;
        private TransformBase? _transform;

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneNodeContext"/> class.
        /// </summary>
        /// <param name="node">The scene node to track.</param>
        public SceneNodeContext(SceneNode node)
        {
            _node = node;

            // Handler for when components are added to the node
            _componentAdded = data =>
            {
                if (data.comp is not null)
                    Track(data.comp);
            };

            // Handler for when components are removed from the node
            _componentRemoved = data =>
            {
                if (data.comp is not null)
                    Untrack(data.comp);
            };

            // Handler for when child transforms are added
            _childAdded = transform =>
            {
                if (transform?.SceneNode is SceneNode childNode)
                    Track(childNode);
            };

            // Handler for when child transforms are removed (currently no-op)
            _childRemoved = _ => { };

            // Subscribe to component events
            _node.ComponentAdded += _componentAdded;
            _node.ComponentRemoved += _componentRemoved;

            // Track all existing components
            foreach (var component in _node.Components)
                Track(component);

            // Set up transform tracking
            AttachTransform(_node.Transform);
        }

        /// <summary>
        /// Attaches tracking to a new transform, detaching from any previous transform.
        /// </summary>
        /// <param name="transform">The transform to attach to, or <c>null</c> to detach only.</param>
        /// <remarks>
        /// Called when the SceneNode's Transform property changes to ensure the new
        /// transform and its children are properly tracked.
        /// </remarks>
        public void AttachTransform(TransformBase? transform)
        {
            // Skip if already attached to this transform
            if (ReferenceEquals(_transform, transform))
                return;

            // Detach from previous transform
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

            // Track the new transform
            Track(_transform);

            // Track all existing child nodes
            var childList = _transform.Children;
            foreach (var child in childList)
            {
                if (child?.SceneNode is SceneNode childNode)
                    Track(childNode);
            }

            // Subscribe to child list changes
            childList.PostAnythingAdded += _childAdded;
            childList.PostAnythingRemoved += _childRemoved;
        }

        /// <summary>
        /// Unsubscribes from all events and cleans up transform tracking.
        /// </summary>
        public void Dispose()
        {
            // Unsubscribe from component events
            _node.ComponentAdded -= _componentAdded;
            _node.ComponentRemoved -= _componentRemoved;

            // Clean up transform tracking
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

    /// <summary>
    /// Per-async-flow context for tracking nested user interaction scopes.
    /// </summary>
    private sealed class UserInteractionContext
    {
        /// <summary>
        /// The current nesting depth of user interaction scopes in this async flow.
        /// </summary>
        public int Depth;
    }

    /// <summary>
    /// Represents a single undoable action entry for display in UI.
    /// </summary>
    /// <param name="Description">A human-readable description of the action.</param>
    /// <param name="TimestampUtc">When the action was recorded (UTC).</param>
    /// <param name="Changes">The list of individual property changes in this action.</param>
    public sealed record UndoEntry(string Description, DateTime TimestampUtc, IReadOnlyList<UndoChangeInfo> Changes);

    /// <summary>
    /// Contains information about a single property change within an undo action.
    /// </summary>
    /// <param name="Target">A weak reference to the changed object (to avoid keeping it alive).</param>
    /// <param name="TargetDisplayName">The display name of the target at the time of the change.</param>
    /// <param name="TargetType">The type of the target object.</param>
    /// <param name="PropertyName">The name of the changed property.</param>
    /// <param name="PreviousValue">The value before the change.</param>
    /// <param name="NewValue">The value after the change.</param>
    public sealed record UndoChangeInfo(WeakReference<XRBase> Target, string TargetDisplayName, Type TargetType, string PropertyName, object? PreviousValue, object? NewValue);

    /// <summary>
    /// Represents a scope for grouping multiple property changes into a single undoable action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Create using <see cref="Undo.BeginChange(string)"/> and dispose when the grouped operation is complete.
    /// All property changes made while the scope is active are accumulated and committed together.
    /// </para>
    /// <para>
    /// Scopes can be canceled via <see cref="Cancel"/> to discard accumulated changes.
    /// </para>
    /// </remarks>
    public sealed class ChangeScope : IDisposable
    {
        /// <summary>
        /// Initializes a new change scope with the given description.
        /// </summary>
        /// <param name="description">The human-readable description for this change group.</param>
        internal ChangeScope(string description)
        {
            Description = description;
            TimestampUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Gets the human-readable description for this change group.
        /// </summary>
        internal string Description { get; }

        /// <summary>
        /// Gets the timestamp (UTC) when the scope was created. Updated when changes are added.
        /// </summary>
        internal DateTime TimestampUtc { get; private set; }

        /// <summary>
        /// Gets the list of property changes accumulated in this scope.
        /// </summary>
        internal List<PropertyChangeStep> Changes { get; } = [];

        /// <summary>
        /// Gets a value indicating whether this scope has been canceled.
        /// Canceled scopes do not commit their changes when disposed.
        /// </summary>
        internal bool IsCanceled { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether this scope has been completed (disposed).
        /// </summary>
        internal bool Completed { get; set; }

        /// <summary>
        /// Cancels this change scope, preventing its changes from being committed.
        /// </summary>
        /// <remarks>
        /// Call this before disposing if you want to discard all accumulated changes
        /// (e.g., if the user cancels an operation).
        /// </remarks>
        public void Cancel() => IsCanceled = true;

        /// <summary>
        /// Adds a new property change or updates an existing one for the same property.
        /// </summary>
        /// <param name="target">The object whose property changed.</param>
        /// <param name="propertyName">The name of the changed property.</param>
        /// <param name="previousValue">The value before the change.</param>
        /// <param name="newValue">The value after the change.</param>
        /// <remarks>
        /// <para>
        /// If a change for the same target and property already exists, it is updated:
        /// <list type="bullet">
        ///   <item><description>If the new value equals the original value, the change is removed (no-op)</description></item>
        ///   <item><description>Otherwise, only the new value is updated (preserving the original)</description></item>
        /// </list>
        /// </para>
        /// <para>
        /// This coalescence ensures that intermediate values during drag operations, etc.,
        /// don't bloat the undo history.
        /// </para>
        /// </remarks>
        internal void AddOrUpdateChange(XRBase target, string propertyName, object? previousValue, object? newValue)
        {
            // Ignore no-op changes
            if (Equals(previousValue, newValue))
                return;

            // Look for existing change to the same property on the same object
            var existing = Changes.FirstOrDefault(c => ReferenceEquals(c.Target, target) && string.Equals(c.PropertyName, propertyName, StringComparison.Ordinal));
            if (existing is not null)
            {
                // If reverting to original, remove the change entirely
                if (Equals(existing.OriginalValue, newValue))
                    Changes.Remove(existing);
                else
                    // Otherwise just update the new value
                    existing.UpdateNewValue(newValue);
            }
            else
            {
                // Add new change
                Changes.Add(new PropertyChangeStep(target, propertyName, previousValue, newValue));
            }

            // Update timestamp to reflect the most recent change
            TimestampUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Completes this scope, committing accumulated changes to the undo stack.
        /// </summary>
        public void Dispose() => CompleteScope(this);
    }

    /// <summary>
    /// Represents a single property change step within an undo action.
    /// </summary>
    /// <param name="target">The object whose property was changed.</param>
    /// <param name="propertyName">The name of the changed property.</param>
    /// <param name="previousValue">The original value before the change.</param>
    /// <param name="newValue">The new value after the change.</param>
    /// <remarks>
    /// <para>
    /// Caches reflection metadata (<see cref="PropertyInfo"/> and <see cref="FieldInfo"/>)
    /// for efficient value restoration during undo/redo.
    /// </para>
    /// <para>
    /// Can restore either the original value (<see cref="ApplyOld"/>) or the new value (<see cref="ApplyNew"/>).
    /// </para>
    /// </remarks>
    internal sealed class PropertyChangeStep(XRBase target, string propertyName, object? previousValue, object? newValue)
    {
        /// <summary>
        /// Cached PropertyInfo for the property, resolved lazily.
        /// </summary>
        private PropertyInfo? _cachedProperty;

        /// <summary>
        /// Cached FieldInfo for the property (fallback if not a property), resolved lazily.
        /// </summary>
        private FieldInfo? _cachedField;

        /// <summary>
        /// Gets the target object whose property was changed.
        /// </summary>
        public XRBase Target { get; } = target;

        /// <summary>
        /// Gets the name of the changed property.
        /// </summary>
        public string PropertyName { get; } = propertyName;

        /// <summary>
        /// Gets the original value before any changes in this scope.
        /// </summary>
        public object? OriginalValue { get; } = previousValue;

        /// <summary>
        /// Gets the current (most recent) new value.
        /// </summary>
        public object? CurrentValue { get; private set; } = newValue;

        /// <summary>
        /// Updates the current new value (used when multiple changes to the same property are coalesced).
        /// </summary>
        /// <param name="value">The new value to set.</param>
        public void UpdateNewValue(object? value) => CurrentValue = value;

        /// <summary>
        /// Applies the original value, undoing the change.
        /// </summary>
        public void ApplyOld() => Apply(OriginalValue);

        /// <summary>
        /// Applies the current new value, redoing the change.
        /// </summary>
        public void ApplyNew() => Apply(CurrentValue);

        /// <summary>
        /// Creates a copy of this step for storage in the undo stack.
        /// </summary>
        /// <returns>A new <see cref="PropertyChangeStep"/> with the same values.</returns>
        public PropertyChangeStep Clone() => new(Target, PropertyName, OriginalValue, CurrentValue);

        /// <summary>
        /// Creates an <see cref="UndoChangeInfo"/> record for this step, for UI display.
        /// </summary>
        /// <returns>An <see cref="UndoChangeInfo"/> containing change details.</returns>
        public UndoChangeInfo ToInfo()
            => new(new WeakReference<XRBase>(Target), GetDisplayName(Target), Target.GetType(), PropertyName, OriginalValue, CurrentValue);

        /// <summary>
        /// Applies a value to the target's property or field.
        /// </summary>
        /// <param name="value">The value to set.</param>
        private void Apply(object? value)
        {
            if (!TrySetValue(value))
            {
                Debug.LogWarning($"Undo failed to set '{PropertyName}' on {Target.GetType().Name}.");
                return;
            }

#if DEBUG || EDITOR
            // Debug warning for transforms without a world (may indicate detached transform)
            if (Target is TransformBase transform && transform.World is null)
            {
                string name = transform.SceneNode?.Name ?? transform.Name ?? transform.GetType().Name;
                Debug.LogWarning($"Undo applied '{PropertyName}' on transform '{name}', but World is null.");
            }
#endif

            // Trigger any post-change processing (e.g., transform matrix recalculation)
            PostApplyChange(Target, PropertyName);
        }

        /// <summary>
        /// Attempts to set the property or field value using reflection.
        /// </summary>
        /// <param name="value">The value to set.</param>
        /// <returns><c>true</c> if the value was set successfully; otherwise, <c>false</c>.</returns>
        private bool TrySetValue(object? value)
        {
            // Try property first
            _cachedProperty ??= ResolveProperty(Target.GetType(), PropertyName);
            if (_cachedProperty is not null && _cachedProperty.CanWrite)
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

            // Fall back to field
            _cachedField ??= ResolveField(Target.GetType(), PropertyName);
            if (_cachedField is not null && !_cachedField.IsInitOnly)
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

        /// <summary>
        /// Performs post-change processing specific to certain target types.
        /// </summary>
        /// <param name="target">The changed target.</param>
        /// <param name="propertyName">The name of the changed property.</param>
        /// <remarks>
        /// For transforms, schedules matrix recalculation.
        /// For SceneNode.Transform changes, schedules the new transform for refresh.
        /// </remarks>
        private static void PostApplyChange(XRBase target, string propertyName)
        {
            switch (target)
            {
                case TransformBase transform:
                    // Schedule transform matrix recalculation
                    ScheduleTransformRefresh(transform);
                    break;
                case SceneNode node when string.Equals(propertyName, nameof(SceneNode.Transform), StringComparison.Ordinal):
                    // If the SceneNode's Transform property changed, refresh the new transform
                    if (node.Transform is TransformBase nodeTransform)
                        ScheduleTransformRefresh(nodeTransform);
                    break;
            }
        }

        /// <summary>
        /// Resolves a property by name, searching up the inheritance hierarchy.
        /// </summary>
        /// <param name="type">The type to start searching from.</param>
        /// <param name="propertyName">The name of the property to find.</param>
        /// <returns>The <see cref="PropertyInfo"/> if found; otherwise, <c>null</c>.</returns>
        private static PropertyInfo? ResolveProperty(Type type, string propertyName)
        {
            while (type is not null)
            {
                var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                // Only return non-indexed properties
                if (prop is not null && prop.GetIndexParameters().Length == 0)
                    return prop;
                type = type.BaseType!;
            }
            return null;
        }

        /// <summary>
        /// Resolves a field by name, searching up the inheritance hierarchy.
        /// </summary>
        /// <param name="type">The type to start searching from.</param>
        /// <param name="fieldName">The name of the field to find.</param>
        /// <returns>The <see cref="FieldInfo"/> if found; otherwise, <c>null</c>.</returns>
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

    /// <summary>
    /// Represents a complete undo action containing one or more property change steps.
    /// </summary>
    /// <param name="description">Human-readable description of the action.</param>
    /// <param name="changes">List of property changes in this action.</param>
    /// <param name="timestampUtc">When the action was created (UTC).</param>
    /// <remarks>
    /// <para>
    /// Undo applies changes in reverse order (LIFO) to properly handle dependencies.
    /// Redo applies changes in forward order (FIFO).
    /// </para>
    /// </remarks>
    private sealed class UndoAction(string description, List<Undo.PropertyChangeStep> changes, DateTime timestampUtc)
    {
        private readonly List<PropertyChangeStep> _changes = changes;

        /// <summary>
        /// Gets the human-readable description of this action.
        /// </summary>
        public string Description { get; } = description;

        /// <summary>
        /// Gets the timestamp (UTC) when this action was created.
        /// </summary>
        public DateTime TimestampUtc { get; } = timestampUtc;

        /// <summary>
        /// Undoes all changes in this action by applying original values in reverse order.
        /// </summary>
        public void Undo()
        {
            // Apply in reverse order to handle any dependencies correctly
            for (int i = _changes.Count - 1; i >= 0; i--)
                _changes[i].ApplyOld();
        }

        /// <summary>
        /// Redoes all changes in this action by applying new values in forward order.
        /// </summary>
        public void Redo()
        {
            foreach (var change in _changes)
                change.ApplyNew();
        }

        /// <summary>
        /// Converts this action to an <see cref="UndoEntry"/> for UI display.
        /// </summary>
        /// <returns>An <see cref="UndoEntry"/> containing action details.</returns>
        public UndoEntry ToEntry()
            => new(Description, TimestampUtc, _changes.Select(c => c.ToInfo()).ToList());
    }

    /// <summary>
    /// Holds keyboard shortcut handler references for a single input interface.
    /// </summary>
    /// <param name="onUndo">Handler for the undo shortcut.</param>
    /// <param name="onRedo">Handler for the redo shortcut.</param>
    private readonly struct ShortcutHandlers(Action onUndo, Action onRedo)
    {
        /// <summary>
        /// Gets the handler for the undo shortcut (Ctrl+Z).
        /// </summary>
        public Action OnUndo { get; } = onUndo;

        /// <summary>
        /// Gets the handler for the redo shortcut (Ctrl+Y).
        /// </summary>
        public Action OnRedo { get; } = onRedo;
    }

    /// <summary>
    /// Identifies the type of keyboard shortcut being handled.
    /// </summary>
    private enum ShortcutKind
    {
        /// <summary>
        /// Undo shortcut (Ctrl+Z).
        /// </summary>
        Undo,

        /// <summary>
        /// Redo shortcut (Ctrl+Y or Ctrl+Shift+Z).
        /// </summary>
        Redo
    }

    #endregion
}
