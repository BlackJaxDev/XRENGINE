namespace XREngine;

public partial class EditorPreferences
{
    private string _nativeDockLayoutsJson = string.Empty;
    private string _nativeDetachedWindowsJson = string.Empty;
    private string _nativeHierarchyStatesJson = string.Empty;
    private object _nativeDockLayoutWriteIntent = new();

    /// <summary>Stores native panel layouts by window identity.</summary>
    public string NativeDockLayoutsJson
    {
        get => _nativeDockLayoutsJson;
        set
        {
            _nativeDockLayoutWriteIntent = new();
            SetField(ref _nativeDockLayoutsJson, value ?? string.Empty);
        }
    }

    /// <summary>Writes an exact layout source without replacing newer callback state.</summary>
    public bool TrySetNativeDockLayoutsJson(string expected, string updated, Func<bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        object intent = new();
        _nativeDockLayoutWriteIntent = intent;
        bool changed = SetFieldValidated(ref _nativeDockLayoutsJson, updated,
            (Owner: this, Intent: intent, Expected: expected, IsCurrent: isCurrent),
            static (state, original, current, next) =>
            {
                if (!ReferenceEquals(state.Owner._nativeDockLayoutWriteIntent, state.Intent) ||
                    !string.Equals(current, state.Expected, StringComparison.Ordinal) || !state.IsCurrent())
                    throw new InvalidOperationException("The saved native layout owner or source changed.");
            }, propertyName: nameof(NativeDockLayoutsJson));
        return (changed || string.Equals(expected, updated, StringComparison.Ordinal)) &&
            ReferenceEquals(_nativeDockLayoutWriteIntent, intent) && isCurrent() &&
            string.Equals(_nativeDockLayoutsJson, updated, StringComparison.Ordinal);
    }

    /// <summary>Stores detached native panel window identities for restoration.</summary>
    public string NativeDetachedWindowsJson
    {
        get => _nativeDetachedWindowsJson;
        set => SetField(ref _nativeDetachedWindowsJson, value ?? string.Empty);
    }

    /// <summary>Stores native hierarchy expansion, scroll, and rename focus by world.</summary>
    public string NativeHierarchyStatesJson
    {
        get => _nativeHierarchyStatesJson;
        set => SetField(ref _nativeHierarchyStatesJson, value ?? string.Empty);
    }
}
