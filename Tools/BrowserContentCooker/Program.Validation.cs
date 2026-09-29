using System.Text.Json;
using System.Text.RegularExpressions;

namespace XREngine.Tools.BrowserContentCooker;

internal static partial class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void Members(JsonElement value, params string[] expected)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Expected a JSON object.");
        HashSet<string> remaining = new(expected, StringComparer.Ordinal);
        foreach (JsonProperty member in value.EnumerateObject())
            Require(remaining.Remove(member.Name), $"Unknown or duplicate property '{member.Name}'.");
        Require(remaining.Count == 0, $"Missing required properties: {string.Join(", ", remaining)}.");
    }

    private static void MembersOptional(JsonElement value, string[] required, string[] optional)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Expected a JSON object.");
        HashSet<string> missing = new(required, StringComparer.Ordinal);
        HashSet<string> allowed = new(required.Concat(optional), StringComparer.Ordinal);
        foreach (JsonProperty member in value.EnumerateObject())
        {
            Require(allowed.Remove(member.Name), $"Unknown or duplicate property '{member.Name}'.");
            missing.Remove(member.Name);
        }
        Require(missing.Count == 0, $"Missing required properties: {string.Join(", ", missing)}.");
    }

    private static uint Unsigned(JsonElement value, uint maximum = uint.MaxValue)
    {
        uint result = 0;
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out result) && result <= maximum,
            "Unsigned integer outside supported range.");
        return result;
    }

    private static uint[] UnsignedArray(JsonElement owner, string name, int maximum, int minimum = 0)
        => Array(owner, name, maximum, minimum).Select(value => Unsigned(value)).ToArray();

    private static JsonElement[] Array(JsonElement owner, string name, int maximum, int minimum = 0)
    {
        JsonElement value = owner.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.Array, $"'{name}' must be an array.");
        Require(value.GetArrayLength() >= minimum && value.GetArrayLength() <= maximum, $"'{name}' count is outside supported limits.");
        return value.EnumerateArray().ToArray();
    }

    private static string Identifier(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String, "Asset ID must be a string.");
        string id = value.GetString()!;
        Require(Regex.IsMatch(id, "^[a-z][a-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant) && !id.Contains("..", StringComparison.Ordinal), "Invalid asset ID.");
        return id;
    }

    private static string[] Ids(JsonElement owner, string name, int maximum)
    {
        string[] values = Array(owner, name, maximum).Select(Identifier).ToArray();
        Require(values.Distinct(StringComparer.Ordinal).Count() == values.Length, $"Duplicate ID in '{name}'.");
        return values;
    }

    private static void ValidateServices(JsonElement services)
    {
        Members(services, "required", "optional");
        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (string list in new[] { "required", "optional" })
        {
            foreach (JsonElement value in Array(services, list, 16))
            {
                Require(value.ValueKind == JsonValueKind.String, "Service requirements must be strings.");
                string service = value.GetString()!;
                Require(Regex.IsMatch(service, "^[a-z][a-z0-9-]{0,63}\\z", RegexOptions.CultureInvariant), "Invalid service requirement token.");
                Require(declared.Add(service), "Service requirements must be unique and required/optional lists must be disjoint.");
            }
        }
    }

    private static string Choice(JsonElement owner, string name, params string[] supported)
    {
        JsonElement value = owner.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.String, $"'{name}' must be a string.");
        string text = value.GetString()!;
        Require(supported.Contains(text, StringComparer.Ordinal), $"Unsupported '{name}': '{text}'.");
        return text;
    }

    private static int Integer(JsonElement value, int minimum, int maximum)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int _), "Expected an integer.");
        int result = value.GetInt32();
        Require(result >= minimum && result <= maximum, "Integer outside supported range.");
        return result;
    }

    private static float Number(JsonElement value, float minimum = -float.MaxValue, float maximum = float.MaxValue)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float _), "Expected a finite float.");
        float result = value.GetSingle();
        Require(float.IsFinite(result) && result >= minimum && result <= maximum, "Float outside supported range.");
        return result;
    }

    private static void Floats(JsonElement owner, string name, int count, float minimum = -float.MaxValue, float maximum = float.MaxValue)
    {
        foreach (JsonElement value in Array(owner, name, count, count)) Number(value, minimum, maximum);
    }

    private static bool Boolean(JsonElement owner, string name)
    {
        JsonElement value = owner.GetProperty(name);
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, $"'{name}' must be Boolean.");
        return value.GetBoolean();
    }

    private static JsonDocument ReadJson(byte[] bytes) => JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });

    private static byte[] ReadBounded(string path, int maximum)
    {
        RejectLinks(path);
        using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(source.Length <= maximum, $"Payload exceeds {maximum} bytes.");
        byte[] result = new byte[checked((int)source.Length)];
        source.ReadExactly(result);
        Require(source.ReadByte() == -1, "Payload changed size while being read.");
        return result;
    }

    private static string SourcePath(string directory, string relative)
    {
        Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative), "Source path must be relative to the recipe.");
        string path = Path.GetFullPath(Path.Combine(directory, relative));
        string under = Path.GetRelativePath(directory, path);
        Require(!Path.IsPathRooted(under) && under != ".." && !under.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Source path escapes recipe directory.");
        RejectLinks(path);
        return path;
    }

    private static void RejectLinks(string path)
    {
        // Reject links in every existing component, including dangling links. Input/output trees must be locally controlled.
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileInfo info = new(current);
            Require(info.LinkTarget is null, "Symbolic link paths are unsupported.");
            if (File.Exists(current) || Directory.Exists(current))
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Reparse point paths are unsupported.");
        }
    }

    private static int ValidateGraph(string id, Dictionary<string, JsonElement> assets, Dictionary<string, string[]> dependencies, HashSet<string> visiting, Dictionary<string, int> heights, int depth)
    {
        Require(depth < 32, "Dependency graph exceeds 32 levels.");
        Require(assets.ContainsKey(id), $"Unknown dependency '{id}'.");
        if (heights.TryGetValue(id, out int cached)) return cached;
        Require(visiting.Add(id), $"Dependency cycle at '{id}'.");
        int height = 1;
        foreach (string dependency in dependencies[id])
            height = Math.Max(height, 1 + ValidateGraph(dependency, assets, dependencies, visiting, heights, depth + 1));
        visiting.Remove(id);
        Require(height <= 32, "Dependency graph exceeds 32 levels.");
        heights.Add(id, height);
        return height;
    }
}
