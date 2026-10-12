using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Networking;

namespace XREngine;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RealtimeJoinHandoffPayload))]
internal sealed partial class DesktopRealtimeJoinHandoffJsonContext : JsonSerializerContext
{
}
