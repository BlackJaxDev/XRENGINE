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

    /// <summary>Updates each target from its own value, preserving other mixed components.</summary>
    public void UpdateValue(Func<object?, object?> update) => Write(null, update);

    private void Write(object? value, Func<object?, object?>? update)
    {
        if (_access.Set is not { } setter)
            return;
        using var interaction = Undo.BeginUserInteraction();
        using var change = Undo.BeginChange($"Set {_property.Name}");
        _writing = true;
        try
        {
            for (int index = 0; index < _targets.Length; index++)
            {
                object target = _targets[index];
                object? oldValue = _access.Get(target);
                object? next = update is null ? value : update(oldValue);
                if (Equals(oldValue, next))
                    continue;
                // A transformer writes the real source inside this same undo group.
                if (target is XRBase tracked && target is not InspectorPropertyEditors.DataTransformerBase)
                    Undo.Track(tracked);
                setter(target, next);
                EditorMutationDirtyState.Mark(target);
            }
        }
        finally
        {
            _writing = false;
            _changed?.Invoke();
        }
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
        if (!_writing && (string.IsNullOrEmpty(propertyName) || propertyName == _property.Name) &&
            Interlocked.Exchange(ref _refreshPending, 1) == 0)
            EditorImGuiUI.EnqueueSceneEdit(_refresh);
    }

    private void RefreshOnOwner()
    {
        Interlocked.Exchange(ref _refreshPending, 0);
        _changed?.Invoke();
    }
}
