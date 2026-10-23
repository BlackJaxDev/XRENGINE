using System.Text.Json;
using XREngine.Editor.Settings;

namespace XREngine.Editor;

/// <summary>Stores native panel layout state in the editor preference asset.</summary>
internal static class NativeDockLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static NativeDockLayout Load(string windowKey)
    {
        var layouts = ReadLayouts();
        return ((layouts.TryGetValue(windowKey, out NativeDockLayout? layout) ? layout : null)
            ?? new NativeDockLayout()).Sanitize();
    }

    public static void Save(string windowKey, NativeDockLayout layout)
    {
        var layouts = ReadLayouts();
        layouts[windowKey] = layout.Sanitize();
        var service = EditorPreferencesService.Current;
        service.GlobalPreferences.NativeDockLayoutsJson = JsonSerializer.Serialize(layouts, JsonOptions);
        service.SaveGlobalPreferences();
    }

    private static Dictionary<string, NativeDockLayout> ReadLayouts()
    {
        string source = EditorPreferencesService.Current.GlobalPreferences.NativeDockLayoutsJson;
        if (string.IsNullOrWhiteSpace(source))
            return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, NativeDockLayout>>(source, JsonOptions)
                ?? new(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            Debug.UI($"[NativeDock] Saved layout is invalid: {exception.Message}");
            return new(StringComparer.Ordinal);
        }
    }
}
