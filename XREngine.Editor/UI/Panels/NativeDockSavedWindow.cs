namespace XREngine.Editor;

/// <summary>Stores one detached window with optional client-area placement.</summary>
internal sealed record NativeDockSavedWindow(string Key, string? PanelId = null, int? Panel = null,
    int? X = null, int? Y = null, int? Width = null, int? Height = null);
