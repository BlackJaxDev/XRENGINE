using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenVR.NET.Manifest;
using XREngine.Components;
using XREngine.Networking;

namespace XREngine;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNameCaseInsensitive = true, IncludeFields = true)]
[JsonSerializable(typeof(DiscoveryAnnouncement))]
[JsonSerializable(typeof(RealtimeJoinHandoffPayload))]
[JsonSerializable(typeof(Engine.VRState.VRInputData))]
public sealed partial class XREngineRuntimeJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true, IncludeFields = true)]
[JsonSerializable(typeof(VrManifestInstallDocument))]
[JsonSerializable(typeof(VrManifest))]
[JsonSerializable(typeof(NameDescription))]
public sealed partial class XREnginePrettyJsonContext : JsonSerializerContext
{
}

public sealed class VrManifestInstallDocument
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = "builtin";

    [JsonPropertyName("applications")]
    public VrManifest[] Applications { get; init; } = [];
}
