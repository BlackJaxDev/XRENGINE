using System.Text.Json.Serialization;
using OpenVR.NET.Manifest;

namespace XREngine;

/// <summary>Applications written to the local OpenVR installation manifest.</summary>
public sealed class VrManifestInstallDocument
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = "builtin";

    [JsonPropertyName("applications")]
    public VrManifest[] Applications { get; init; } = [];
}
