using System.Text.Json.Serialization;

namespace XREngine.Rendering;

/// <summary>Roots only the portable scene wire DTO and its nested resource records.</summary>
[JsonSerializable(typeof(BrowserSceneDto))]
internal sealed partial class BrowserSceneJsonContext : JsonSerializerContext { }
