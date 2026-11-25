using System.Text.Json;
using XREngine.Editor.Settings;

namespace XREngine.Editor;

internal sealed record NativeHierarchyState(
    Guid[] CollapsedNodes,
    Guid[] CollapsedSections,
    float ScrollOffset,
    bool ShowEditorScene,
    Guid? FocusedRename);

internal static class NativeHierarchyStateStore
{
    private const int MaxWorlds = 32;

    public static NativeHierarchyState? Load(Guid worldId)
    {
        if (worldId == Guid.Empty)
            return null;
        return Read().GetValueOrDefault(worldId.ToString("N"));
    }

    public static void Save(Guid worldId, NativeHierarchyState state)
    {
        if (worldId == Guid.Empty)
            return;
        Dictionary<string, NativeHierarchyState> states = Read();
        string key = worldId.ToString("N");
        if (!states.ContainsKey(key) && states.Count >= MaxWorlds)
            states.Remove(states.Keys.First());
        states[key] = state;
        EditorPreferencesService service = EditorPreferencesService.Current;
        service.GlobalPreferences.NativeHierarchyStatesJson = JsonSerializer.Serialize(states);
        service.SaveGlobalPreferences();
    }

    private static Dictionary<string, NativeHierarchyState> Read()
    {
        string json = EditorPreferencesService.Current.GlobalPreferences.NativeHierarchyStatesJson;
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, NativeHierarchyState>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, NativeHierarchyState>>(json)
                ?? new Dictionary<string, NativeHierarchyState>(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            Debug.UI($"[NativeHierarchy] Saved state is invalid: {exception.Message}");
            return new Dictionary<string, NativeHierarchyState>(StringComparer.Ordinal);
        }
    }
}
