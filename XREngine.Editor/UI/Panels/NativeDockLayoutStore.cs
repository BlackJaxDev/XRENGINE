using XREngine.Editor.Settings;

namespace XREngine.Editor;

/// <summary>Stores native panel layout state in the editor preference asset.</summary>
internal static class NativeDockLayoutStore
{
    private static readonly NativeDockLayoutPersistence Persistence = new(
        () => EditorPreferencesService.Current.GlobalPreferences.NativeDockLayoutsJson,
        (expected, updated, isCurrent) =>
        {
            var service = EditorPreferencesService.Current;
            var preferences = service.GlobalPreferences;
            if (!preferences.TrySetNativeDockLayoutsJson(expected, updated,
                () => ReferenceEquals(service.GlobalPreferences, preferences) && isCurrent()))
                throw new InvalidOperationException("The native layout preference write was not accepted.");
            service.SaveGlobalPreferences();
        });

    public static NativeDockLayout Load(string windowKey, string? currentWindowTitle = null)
        => Persistence.Load(windowKey, currentWindowTitle);

    public static bool HasLayout(string windowKey) => Persistence.HasLayout(windowKey);

    public static void MigrateLegacyDetached(string windowKey, int fallbackPanel)
        => Persistence.MigrateLegacyDetached(windowKey, fallbackPanel);

    /// <summary>Saves the admitted record. Returns false when stored data or ownership refuses the save.</summary>
    public static bool Save(string windowKey, NativeDockLayout layout)
        => Persistence.Save(windowKey, layout);
}
