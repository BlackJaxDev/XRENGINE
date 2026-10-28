using System.Text.Json;
using XREngine.Editor.Settings;

namespace XREngine.Editor;

/// <summary>
/// Stores native panel layout state in the editor preference asset. A save keeps every other
/// record. A save is refused while the stored data is malformed, so invalid data never causes
/// an automatic rewrite.
/// </summary>
internal static class NativeDockLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Loads the layout for <paramref name="windowKey"/>. For <see cref="NativeDockLayoutKeys.Primary"/>,
    /// <paramref name="primaryTitle"/> selects a legacy record when no canonical record exists.
    /// </summary>
    public static NativeDockLayout Load(string windowKey, string? primaryTitle = null)
    {
        ReadLayouts(out var layouts);
        NativeDockLayout? layout;
        if (windowKey == NativeDockLayoutKeys.Primary)
        {
            ENativeDockLayoutKeyMatch match = NativeDockLayoutKeys.ResolvePrimary(
                layouts, primaryTitle, out layout, out int unmatched);
            if (match == ENativeDockLayoutKeyMatch.Unmatched)
                Debug.UIWarning($"[NativeDock] {unmatched} saved primary layout(s) do not match the window title '{primaryTitle}'. " +
                    "The default layout is used. The old records are kept; arrange the layout again to save it.");
        }
        else
            layout = layouts.TryGetValue(windowKey, out NativeDockLayout? found) ? found : null;
        return (layout ?? new NativeDockLayout()).Sanitize();
    }

    public static bool HasLayout(string windowKey)
    {
        ReadLayouts(out var layouts);
        return layouts.TryGetValue(windowKey, out NativeDockLayout? layout) && layout is not null;
    }

    public static void MigrateLegacyDetached(string windowKey, int fallbackPanel)
    {
        NativeDockLayout layout = Load(windowKey);
        layout.DockTree = NativeDockTree.FromLegacyDetached(layout, fallbackPanel);
        Save(windowKey, layout);
    }

    /// <summary>Saves one record and keeps all other records. Returns false when the save was refused.</summary>
    public static bool Save(string windowKey, NativeDockLayout layout)
    {
        if (!ReadLayouts(out var layouts))
        {
            Debug.UIWarning("[NativeDock] The layout was not saved because the stored layout data is malformed. " +
                "Repair or clear NativeDockLayoutsJson in the global editor preferences.");
            return false;
        }
        layouts[windowKey] = layout.Sanitize();
        var service = EditorPreferencesService.Current;
        service.GlobalPreferences.NativeDockLayoutsJson = JsonSerializer.Serialize(layouts, JsonOptions);
        service.SaveGlobalPreferences();
        return true;
    }

    /// <summary>Reads all records. Returns false when the stored data is malformed.</summary>
    private static bool ReadLayouts(out Dictionary<string, NativeDockLayout?> layouts)
    {
        string source = EditorPreferencesService.Current.GlobalPreferences.NativeDockLayoutsJson;
        if (string.IsNullOrWhiteSpace(source))
        {
            layouts = new(StringComparer.Ordinal);
            return true;
        }
        try
        {
            layouts = JsonSerializer.Deserialize<Dictionary<string, NativeDockLayout?>>(source, JsonOptions)
                ?? new(StringComparer.Ordinal);
            return true;
        }
        catch (JsonException exception)
        {
            Debug.UI($"[NativeDock] Saved layout is invalid: {exception.Message}");
            layouts = new(StringComparer.Ordinal);
            return false;
        }
    }
}
