using System.Text.Json.Serialization;

namespace XREngine.Browser;

/// <summary>Statically roots the cooked wire contract for browser trimming and AOT.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(BrowserCookedMeshDto))]
[JsonSerializable(typeof(BrowserCookedMaterialDto))]
[JsonSerializable(typeof(BrowserCookedTextureDto))]
[JsonSerializable(typeof(BrowserCookedSceneDto))]
[JsonSerializable(typeof(BrowserCookedAnimationDto))]
[JsonSerializable(typeof(BrowserCookedCollisionDto))]
[JsonSerializable(typeof(BrowserCookedContentStatistics))]
internal sealed partial class BrowserCookedJsonContext : JsonSerializerContext { }
