using System.Text;
using System.Text.Json;
using XREngine.Rendering;

namespace XREngine.Browser;

/// <summary>Versioned cooked-scene identity around the bounded portable snapshot payload.</summary>
internal static class BrowserSceneEnvelope
{
    private const string Schema = "xre.browser.scene.v1";
    private const int MaxJsonBytes = 16 * 1024 * 1024;

    internal static string Serialize(BrowserSceneSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // The payload is already valid JSON from the exact source-generated DTO serializer.
        // Embed it as an object, preserving the existing flat snapshot representation.
        string json = string.Concat(
            "{\"schema\":\"", Schema,
            "\",\"serializer\":\"", BrowserStaticRegistrations.SceneJsonId,
            "\",\"snapshot\":\"", BrowserStaticRegistrations.SnapshotId,
            "\",\"component\":\"", BrowserStaticRegistrations.MeshComponentId,
            "\",\"transform\":\"", BrowserStaticRegistrations.TransformId,
            "\",\"mesh\":\"", BrowserStaticRegistrations.MeshId,
            "\",\"material\":\"", BrowserStaticRegistrations.MaterialId,
            "\",\"texture\":\"", BrowserStaticRegistrations.TextureId,
            "\",\"payload\":", snapshot.ToJson(), "}");
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            throw new NotSupportedException("Cooked browser scene JSON exceeds 16 MiB.");
        return json;
    }

    internal static BrowserSceneSnapshot Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxJsonBytes || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            throw new ArgumentException("Cooked browser scene JSON exceeds 16 MiB.", nameof(json));

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Browser scene JSON must be an object.", nameof(json));
            // Flat v1 snapshots remain valid; their own DTO parser rejects unknown fields.
            if (!root.TryGetProperty("schema", out _))
                return BrowserSceneSnapshot.FromJson(json);

            HashSet<string> fields = new(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!fields.Add(property.Name) || property.Name is not
                    ("schema" or "serializer" or "snapshot" or "component" or "transform" or
                     "mesh" or "material" or "texture" or "payload"))
                    throw new ArgumentException("Cooked browser scene has duplicate or unknown fields.", nameof(json));
            }
            if (fields.Count != 9 ||
                !Matches(root, "schema", Schema) ||
                !Matches(root, "serializer", BrowserStaticRegistrations.SceneJsonId) ||
                !Matches(root, "snapshot", BrowserStaticRegistrations.SnapshotId) ||
                !Matches(root, "component", BrowserStaticRegistrations.MeshComponentId) ||
                !Matches(root, "transform", BrowserStaticRegistrations.TransformId) ||
                !Matches(root, "mesh", BrowserStaticRegistrations.MeshId) ||
                !Matches(root, "material", BrowserStaticRegistrations.MaterialId) ||
                !Matches(root, "texture", BrowserStaticRegistrations.TextureId) ||
                root.GetProperty("payload").ValueKind != JsonValueKind.Object)
                throw new NotSupportedException("Cooked browser scene schema or type ID is unsupported.");
            return BrowserSceneSnapshot.FromJson(root.GetProperty("payload").GetRawText());
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Cooked browser scene JSON is invalid.", nameof(json), exception);
        }
    }

    private static bool Matches(JsonElement root, string name, string expected)
    {
        JsonElement value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }
}
