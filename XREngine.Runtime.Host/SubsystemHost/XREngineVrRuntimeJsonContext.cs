using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNameCaseInsensitive = true, IncludeFields = true)]
[JsonSerializable(typeof(RuntimeVrState.VRInputData), TypeInfoPropertyName = "RuntimeVrInputData")]
/// <summary>Source-generated JSON metadata for the VR input transport.</summary>
public sealed partial class XREngineVrRuntimeJsonContext : JsonSerializerContext
{
}

