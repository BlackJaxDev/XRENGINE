using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XREngine.Editor.MaterialAuthoring;

/// <summary>
/// Versioned local authoring state. The schema fingerprint in the key naturally
/// invalidates stale expansion data after a source/schema upgrade.
/// </summary>
public sealed class MaterialAuthoringPersistence
{
    private const int CurrentVersion = 1;
    private readonly Dictionary<string, HashSet<string>> _expanded =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _notes =
        new(StringComparer.Ordinal);
    private bool _loaded;

    public static MaterialAuthoringPersistence Instance { get; } = new();

    public bool IsExpanded(string schemaFingerprint, string semanticId, bool defaultValue)
    {
        EnsureLoaded();
        string key = BuildSchemaUserKey(schemaFingerprint);
        return _expanded.TryGetValue(key, out HashSet<string>? values)
            ? values.Contains(semanticId)
            : defaultValue;
    }

    public void SetExpanded(string schemaFingerprint, string semanticId, bool expanded)
    {
        EnsureLoaded();
        string key = BuildSchemaUserKey(schemaFingerprint);
        if (!_expanded.TryGetValue(key, out HashSet<string>? values))
            _expanded[key] = values = new(StringComparer.Ordinal);

        if (expanded)
            values.Add(semanticId);
        else
            values.Remove(semanticId);
        Save();
    }

    public string? GetNote(string materialIdentity, string semanticId)
    {
        EnsureLoaded();
        _notes.TryGetValue($"{materialIdentity}|{semanticId}", out string? note);
        return note;
    }

    public void SetNote(string materialIdentity, string semanticId, string? note)
    {
        EnsureLoaded();
        string key = $"{materialIdentity}|{semanticId}";
        if (string.IsNullOrWhiteSpace(note))
            _notes.Remove(key);
        else
            _notes[key] = note.Trim();
        Save();
    }

    private static string BuildSchemaUserKey(string schemaFingerprint)
        => $"{Environment.UserName}|{schemaFingerprint}";

    private static string GetPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "XRENGINE", "Editor", "material-authoring-v1.json");
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;

        string path = GetPath();
        if (!File.Exists(path))
            return;

        try
        {
            State? state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            if (state?.Version != CurrentVersion)
                return;

            foreach ((string key, string[] values) in state.Expanded)
                _expanded[key] = new(values, StringComparer.Ordinal);
            foreach ((string key, string value) in state.Notes)
                _notes[key] = value;
        }
        catch
        {
            // Corrupt or incompatible local editor state must never block an
            // asset from opening. A later successful mutation rewrites it.
        }
    }

    private void Save()
    {
        string path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Dictionary<string, string[]> expanded = new(StringComparer.Ordinal);
        foreach ((string key, HashSet<string> values) in _expanded)
            expanded[key] = [.. values.Order(StringComparer.Ordinal)];

        State state = new()
        {
            Version = CurrentVersion,
            Expanded = expanded,
            Notes = new(_notes, StringComparer.Ordinal),
        };
        string temporaryPath = $"{path}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
        File.Move(temporaryPath, path, true);
    }

    private sealed class State
    {
        public int Version { get; init; }
        public Dictionary<string, string[]> Expanded { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Notes { get; init; } = new(StringComparer.Ordinal);
    }
}
