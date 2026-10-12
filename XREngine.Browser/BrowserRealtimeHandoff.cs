using System.Text;
using System.Text.Json;
using XREngine.Networking;

namespace XREngine.Browser;

/// <summary>Bounds the transient interop payload while retaining the production handoff and serializer contract.</summary>
internal static class BrowserRealtimeHandoff
{
    private const int MaximumBytes = 16 * 1024;

    internal static RealtimeJoinHandoffPayload Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumBytes || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw Invalid();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateMembers(document.RootElement);
            return JsonSerializer.Deserialize(document.RootElement, BrowserRealtimeJsonContext.Default.RealtimeJoinHandoffPayload)
                ?? throw Invalid();
        }
        catch (JsonException)
        {
            // Do not retain an inner exception: JSON diagnostics can include
            // user-controlled property names from a credential-bearing payload.
            throw Invalid();
        }
    }

    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Invalid();
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray())
                RejectDuplicateMembers(child);
    }

    private static InvalidDataException Invalid()
        => new("BrowserNetwork.InvalidHandoff: expected one bounded shared managed-admission object.");
}
