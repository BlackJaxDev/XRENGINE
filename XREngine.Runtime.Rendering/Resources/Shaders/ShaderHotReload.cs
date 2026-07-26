namespace XREngine.Rendering;

/// <summary>
/// Public renderer-development control surface for shader dependency reload.
/// </summary>
public static class ShaderHotReload
{
    public static int DebounceMilliseconds
    {
        get => ShaderSourceDependencyIndex.DebounceMilliseconds;
        set => ShaderSourceDependencyIndex.DebounceMilliseconds = value;
    }

    public static long PublishedInvalidations => ShaderSourceDependencyIndex.NotificationsPublished;
    public static long StaleNotificationsRejected => ShaderSourceDependencyIndex.StaleNotificationsRejected;

    public static int ReloadAll(string reason = "manual shader reload")
    {
        ShaderSourceResolver.ClearCaches();
        return ShaderSourceDependencyIndex.InvalidateAll(reason);
    }
}

