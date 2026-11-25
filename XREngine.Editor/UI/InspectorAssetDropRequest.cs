using System.ComponentModel;
using System.Reflection;
using XREngine.Data.Core;

namespace XREngine.Editor.UI;

/// <summary>Owns one asynchronous asset assignment through capture, notification, and Undo settlement.</summary>
internal sealed class InspectorAssetDropRequest : IEditorScalarWriteObserver
{
    private readonly InspectorPropertyDropBinding _binding;
    private readonly Func<bool> _isPending;
    private readonly List<Action> _unsubscribe = [];
    private readonly Dictionary<object, int> _indices = new(ReferenceEqualityComparer.Instance);
    private readonly object?[] _expected;
    private int _invalidated;
    private int _completionState;
    private int _enteredIndex = -1;
    private int _setterThread;
    private object? _setterPrevious;
    private object? _setterNext;
    private bool _ownNotification;
    private bool _capturing;
    private bool _captureStarted;

    public InspectorAssetDropRequest(InspectorPropertyDropBinding binding, Func<bool> isPending)
    {
        _binding = binding;
        _isPending = isPending;
        _expected = new object?[binding.Targets.Length];
        for (int index = 0; index < binding.Targets.Length; index++)
            if (!_indices.TryAdd(binding.Targets[index]!, index))
                throw new InvalidOperationException("An asset drop contains the same target twice.");
        Access = new(binding.Property, binding.Targets);
    }

    public PropertyInfo Property => _binding.Property;
    public EditorPropertyAccess Access { get; }
    public void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);

    public void RequireCurrent()
    {
        if (Volatile.Read(ref _invalidated) != 0 || Volatile.Read(ref _completionState) == 2 ||
            !_isPending())
            throw new InvalidOperationException("The pending asset drop no longer owns this assignment.");
        _binding.RequireCurrent();
    }

    public void ObserveAndCapture()
    {
        RequireCurrent();
        if (_captureStarted)
            throw new InvalidOperationException("The asset drop was already captured.");
        _captureStarted = true;
        _capturing = true;
        try
        {
        foreach (object? target in _binding.Targets)
        {
            if (target is IXRNotifyPropertyChanged engineObject)
            {
                XRPropertyChangedEventHandler handler = (sender, args) => OnEngineChanged(target, sender, args);
                // Retain cleanup before an application-defined add accessor can run.
                _unsubscribe.Add(() => engineObject.PropertyChanged -= handler);
                engineObject.PropertyChanged += handler;
            }
            else if (target is INotifyPropertyChanged observable)
            {
                PropertyChangedEventHandler handler = (_, args) =>
                {
                    if (IsRelevant(args.PropertyName))
                        Invalidate();
                };
                _unsubscribe.Add(() => observable.PropertyChanged -= handler);
                observable.PropertyChanged += handler;
            }
            RequireCurrent();
        }
        for (int index = 0; index < _binding.Targets.Length; index++)
        {
            RequireCurrent();
            object? value = Property.GetValue(_binding.Targets[index]);
            RequireCurrent();
            _expected[index] = value;
        }
        }
        finally
        {
            _capturing = false;
            if (Volatile.Read(ref _invalidated) != 0)
                StopObserving();
        }
    }

    public bool TryBeginCompletion()
        => Interlocked.CompareExchange(ref _completionState, 1, 0) == 0;

    public void ValidateValues()
    {
        for (int index = 0; index < _binding.Targets.Length; index++)
        {
            RequireCurrent();
            object? value = Property.GetValue(_binding.Targets[index]);
            RequireCurrent();
            if (!ReferenceEquals(value, _expected[index]))
            {
                Invalidate();
                throw new InvalidOperationException("The asset reference changed before assignment completed.");
            }
        }
    }

    public void ValidateValue(object target, object? current)
    {
        RequireCurrent();
        if (!_indices.TryGetValue(target, out int index) || !ReferenceEquals(current, _expected[index]))
        {
            Invalidate();
            throw new InvalidOperationException("The asset setter no longer matches its captured reference.");
        }
    }

    public void EnterSetter(object target, object? previous, object? next)
    {
        ValidateValue(target, previous);
        if (_enteredIndex != -1)
        {
            Invalidate();
            throw new InvalidOperationException("An asset setter is already active for this drop.");
        }
        int index = _indices[target];
        _enteredIndex = index;
        _setterThread = Environment.CurrentManagedThreadId;
        _setterPrevious = previous;
        _setterNext = next;
        _ownNotification = false;
    }

    public void ExitSetter(object target, bool returnedNormally)
    {
        int index = _enteredIndex;
        if (index >= 0 && ReferenceEquals(_binding.Targets[index], target))
        {
            if (returnedNormally)
                _expected[index] = _setterNext;
            else
                Invalidate();
        }
        else
            Invalidate();
        _enteredIndex = -1;
        _setterThread = 0;
        _setterPrevious = null;
        _setterNext = null;
        _ownNotification = false;
    }

    private void OnEngineChanged(object? observed, object? sender, IXRPropertyChangedEventArgs args)
    {
        if (!IsRelevant(args.PropertyName))
            return;
        if (_enteredIndex >= 0 && !_ownNotification &&
            Environment.CurrentManagedThreadId == _setterThread &&
            ReferenceEquals(observed, _binding.Targets[_enteredIndex]) &&
            ReferenceEquals(sender, observed) && args.PropertyName == Property.Name &&
            ReferenceEquals(args.PreviousValue, _setterPrevious) &&
            ReferenceEquals(args.NewValue, _setterNext))
        {
            _ownNotification = true;
            return;
        }
        Invalidate();
    }

    private bool IsRelevant(string? name)
        => string.IsNullOrEmpty(name) || name == Property.Name ||
            name is "ID" or "World" or "SceneNode" or "Parent" or "Transform" or
                "IsDestroyed" or "IsDestroyQueued";

    public void Retire()
    {
        Invalidate();
        // The admitted setter must settle Undo and dirty state with observers present.
        if (!_capturing && Volatile.Read(ref _completionState) != 1)
            StopObserving();
    }

    public void Complete()
    {
        Interlocked.Exchange(ref _completionState, 2);
        StopObserving();
    }

    private void StopObserving()
    {
        Action[] callbacks = [.. _unsubscribe];
        _unsubscribe.Clear();
        foreach (Action unsubscribe in callbacks)
            try { unsubscribe(); }
            catch (Exception error) { Debug.LogException(error, "Could not detach an asset-drop observer."); }
    }
}
