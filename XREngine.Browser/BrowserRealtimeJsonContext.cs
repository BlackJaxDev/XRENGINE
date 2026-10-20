using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Networking;

namespace XREngine.Browser;

/// <summary>Static browser serializer metadata for the shared production admission handoff.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RealtimeJoinHandoffPayload))]
internal sealed partial class BrowserRealtimeJsonContext : JsonSerializerContext
{
}
