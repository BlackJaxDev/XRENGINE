using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using XREngine.Data.Core;

namespace XREngine.Editor.UI;

/// <summary>Owns cached property access, active notifications, and grouped editor mutations.</summary>
internal sealed class EditorPropertyAccess
{
    private static readonly ConditionalWeakTable<PropertyInfo, EditorPropertyDelegates> Cache = new();
    private readonly PropertyInfo _property;
    private readonly EditorPropertyDelegates _access;
    private readonly object[] _targets;
    private readonly Action _refresh;
    private Action? _changed;
    private int _refreshPending;
    private bool _writing;

    public EditorPropertyAccess(PropertyInfo property, object?[] targets)
    {
        _property = property;
        _access = Cache.GetValue(property, EditorPropertyDelegates.Create);
        _targets = targets.Where(target => target is not null).Cast<object>().ToArray();
        for (int index = 0; index < _targets.Length; index++)
            if (!property.DeclaringType!.IsInstanceOfType(_targets[index]))
                throw new ArgumentException("An editor target does not declare the selected property.", nameof(targets));
        _refresh = RefreshOnOwner;
    }

    public bool CanWrite => _access.Set is not null;

    /// <summary>Returns false when the targets have different property values.</summary>
    public bool TryGetCommonValue(out object? value)
    {
        value = _targets.Length == 0 ? null : _access.Get(_targets[0]);
        for (int index = 1; index < _targets.Length; index++)
            if (!Equals(value, _access.Get(_targets[index])))
                return false;
        return true;
    }

    public event Action? Changed
    {
        add
        {
            bool attach = _changed is null;
            _changed += value;
            if (attach && _changed is not null)
                Subscribe();
        }
        remove
        {
            bool detach = _changed is not null;
            _changed -= value;
            if (detach && _changed is null)
                Unsubscribe();
        }
    }

    public void SetValue(object? value) => Write(value, null);

    /// <summary>Checks a retained caller's ownership before entering each new write.</summary>
    public void SetValue(object? value, Action validateWrite) => Write(value, null, validateWrite);

    /// <summary>Assigns an exact reference with optional notification attribution for a retained drop.</summary>
    public void SetReferenceValue(object? value, Action validateWrite,
        IEditorScalarWriteObserver? observer = null)
        => Write(value, null, validateWrite, observer, referenceEquality: true);

    /// <summary>Updates each target from its own value, preserving other mixed components.</summary>
    public void UpdateValue(Func<object?, object?> update) => Write(null, update);

    private void Write(object? value, Func<object?, object?>? update, Action? validateWrite = null,
        IEditorScalarWriteObserver? observer = null, bool referenceEquality = false)
    {
        validateWrite?.Invoke();
        if (_access.Set is not { } setter)
            return;
        if (referenceEquality && _targets.Any(target => target is IEditorListElementBinding or
                IEditorLinkedElementBinding or IEditorDictionaryElementBinding))
            throw new InvalidOperationException("A reference drop requires direct scalar property owners.");
        if (_targets.Any(target => target is IEditorNestedListElementBinding))
        {
            if (!_targets.All(target => target is IEditorNestedListElementBinding))
                throw new InvalidOperationException("The nested list targets do not share one mutation route.");
            IEditorNestedListElementBinding[] nested = _targets.Cast<IEditorNestedListElementBinding>().ToArray();
            if (nested.Any(binding => binding.Index != nested[0].Index ||
                !ReferenceEquals(binding.Paths, nested[0].Paths) ||
                !ReferenceEquals(binding.MutationContext, nested[0].MutationContext)))
                throw new InvalidOperationException("The nested list targets do not share one source path.");
            EditorNestedListMutation.Apply(nested[0].Paths, EEditorListMutation.Set,
                nested[0].Index, ResolveNext, nested[0].MutationContext,
                () => nested.All(binding => binding.IsRowCurrent), nested);
            _changed?.Invoke();
            return;
        }
        if (_targets.Any(target => target is IEditorListElementBinding))
        {
            if (!_targets.All(target => target is IEditorListElementBinding))
                throw new InvalidOperationException("The list editor targets do not share one mutation route.");
            EditorListMutation.Set(_targets.Cast<IEditorListElementBinding>().ToArray(),
                ResolveNext);
            _changed?.Invoke();
            return;
        }
        if (_targets.Any(target => target is IEditorLinkedElementBinding))
        {
            if (!_targets.All(target => target is IEditorLinkedElementBinding))
                throw new InvalidOperationException("The linked-list editor targets do not share one mutation route.");
            EditorLinkedMutation.Set(_targets.Cast<IEditorLinkedElementBinding>().ToArray(),
                ResolveNext,
                ((IEditorLinkedElementBinding)_targets[0]).Refresh);
            _changed?.Invoke();
            return;
        }
        if (_targets.Any(target => target is IEditorDictionaryElementBinding))
        {
            if (!_targets.All(target => target is IEditorDictionaryElementBinding))
                throw new InvalidOperationException("The dictionary editor targets do not share one mutation route.");
            EditorDictionaryMutation.Set(_targets.Cast<IEditorDictionaryElementBinding>().ToArray(),
                ResolveNext,
                ((IEditorDictionaryElementBinding)_targets[0]).Refresh);
            _changed?.Invoke();
            return;
        }
        using var interaction = Undo.BeginUserInteraction();
        using var change = Undo.BeginChange($"Set {_property.Name}");
        _writing = true;
        try
        {
            for (int index = 0; index < _targets.Length; index++)
            {
                validateWrite?.Invoke();
                object target = _targets[index];
                object? oldValue = _access.Get(target);
                object? next = ResolveNext(oldValue);
                observer?.ValidateValue(target, oldValue);
                if (referenceEquality ? ReferenceEquals(oldValue, next) : Equals(oldValue, next))
                    continue;
                // A transformer writes the real source inside this same undo group.
                if (target is XRBase tracked && target is not InspectorPropertyEditors.DataTransformerBase)
                    Undo.Track(tracked);
                validateWrite?.Invoke();
                observer?.EnterSetter(target, oldValue, next);
                bool returned = false;
                try
                {
                    setter(target, next);
                    returned = true;
                }
                finally { observer?.ExitSetter(target, returned); }
                if (target is not XRBase)
                {
                    // Plain objects do not publish the engine events used by Undo.Track.
                    object? committedValue = _access.Get(target);
                    if (referenceEquality ? !ReferenceEquals(oldValue, committedValue) : !Equals(oldValue, committedValue))
                        Undo.RecordStructuralChange($"Set {_property.Name}",
                            () => RestoreValue(target, oldValue),
                            () => RestoreValue(target, committedValue));
                }
                EditorMutationDirtyState.Mark(target);
            }
        }
        finally
        {
            _writing = false;
            _changed?.Invoke();
        }

        object? ResolveNext(object? oldValue)
        {
            validateWrite?.Invoke();
            object? next = update is null ? value : update(oldValue);
            validateWrite?.Invoke();
            return next;
        }
    }

    private void RestoreValue(object target, object? value)
    {
        _access.Set!(target, value);
        EditorMutationDirtyState.Mark(target);
        Notify(_property.Name);
    }

    private void Subscribe()
    {
        foreach (object target in _targets)
            if (target is IXRNotifyPropertyChanged engineObject)
                engineObject.PropertyChanged += EnginePropertyChanged;
            else if (target is INotifyPropertyChanged observable)
                observable.PropertyChanged += PropertyChanged;
    }

    private void Unsubscribe()
    {
        foreach (object target in _targets)
            if (target is IXRNotifyPropertyChanged engineObject)
                engineObject.PropertyChanged -= EnginePropertyChanged;
            else if (target is INotifyPropertyChanged observable)
                observable.PropertyChanged -= PropertyChanged;
    }

    private void EnginePropertyChanged(object? sender, IXRPropertyChangedEventArgs args) => Notify(args.PropertyName);
    private void PropertyChanged(object? sender, PropertyChangedEventArgs args) => Notify(args.PropertyName);

    private void Notify(string? propertyName)
    {
        if (!_writing && _changed is not null &&
            (string.IsNullOrEmpty(propertyName) || propertyName == _property.Name) &&
            Interlocked.Exchange(ref _refreshPending, 1) == 0)
            EditorImGuiUI.EnqueueSceneEdit(_refresh);
    }

    private void RefreshOnOwner()
    {
        Interlocked.Exchange(ref _refreshPending, 0);
        _changed?.Invoke();
    }
}
