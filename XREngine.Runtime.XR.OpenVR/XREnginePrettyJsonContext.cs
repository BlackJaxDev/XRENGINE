using System.Text.Json.Serialization;
using OpenVR.NET.Manifest;

namespace XREngine;

/// <summary>Source-generated JSON metadata for OpenVR application manifests.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true, IncludeFields = true)]
[JsonSerializable(typeof(VrManifestInstallDocument))]
[JsonSerializable(typeof(VrManifest))]
[JsonSerializable(typeof(NameDescription))]
public sealed partial class XREnginePrettyJsonContext : JsonSerializerContext
{
}
