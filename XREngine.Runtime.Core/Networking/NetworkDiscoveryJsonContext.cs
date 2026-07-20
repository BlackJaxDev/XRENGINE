using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Components;

namespace XREngine.Networking;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DiscoveryAnnouncement))]
internal sealed partial class NetworkDiscoveryJsonContext : JsonSerializerContext
{
}