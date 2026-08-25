using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using XREngine.Components;
using XREngine.Networking;

namespace XREngine;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNameCaseInsensitive = true, IncludeFields = true)]
[JsonSerializable(typeof(DiscoveryAnnouncement))]
[JsonSerializable(typeof(RealtimeJoinHandoffPayload))]
public sealed partial class XREngineRuntimeJsonContext : JsonSerializerContext
{
}
