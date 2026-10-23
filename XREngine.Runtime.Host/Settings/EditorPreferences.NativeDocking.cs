namespace XREngine;

public partial class EditorPreferences
{
    private string _nativeDockLayoutsJson = string.Empty;
    private string _nativeDetachedWindowsJson = string.Empty;
    private string _nativeHierarchyStatesJson = string.Empty;

    /// <summary>Stores native panel layouts by window identity.</summary>
    public string NativeDockLayoutsJson
    {
        get => _nativeDockLayoutsJson;
        set => SetField(ref _nativeDockLayoutsJson, value ?? string.Empty);
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
