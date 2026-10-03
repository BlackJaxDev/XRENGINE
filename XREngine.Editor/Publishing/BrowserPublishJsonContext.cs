using System.Text.Json.Serialization;
using XREngine.Browser;

namespace XREngine.Editor.Publishing;

/// <summary>Uses the runtime's cooked contracts for native authoring export.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrowserCookedMeshDto))]
[JsonSerializable(typeof(BrowserCookedMaterialDto))]
[JsonSerializable(typeof(BrowserCookedTextureDto))]
[JsonSerializable(typeof(BrowserCookedSceneDto))]
[JsonSerializable(typeof(BrowserCookedAnimationDto))]
internal partial class BrowserPublishJsonContext : JsonSerializerContext
{
}
