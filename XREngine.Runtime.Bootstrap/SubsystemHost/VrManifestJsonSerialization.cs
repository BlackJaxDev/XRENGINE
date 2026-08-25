using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVR.NET.Manifest;

namespace XREngine;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true, IncludeFields = true)]
[JsonSerializable(typeof(VrManifestInstallDocument))]
[JsonSerializable(typeof(VrManifest))]
[JsonSerializable(typeof(NameDescription))]
public sealed partial class XREnginePrettyJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNameCaseInsensitive = true, IncludeFields = true)]
[JsonSerializable(typeof(RuntimeVrState.VRInputData), TypeInfoPropertyName = "RuntimeVrInputData")]
public sealed partial class XREngineVrRuntimeJsonContext : JsonSerializerContext
{
}

public sealed class VrManifestInstallDocument
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = "builtin";

    [JsonPropertyName("applications")]
    public VrManifest[] Applications { get; init; } = [];
}
