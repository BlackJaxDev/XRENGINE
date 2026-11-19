using System.Security.Cryptography;
using System.Text.Json;

namespace XREngine.UnityScriptHost.Compilation;

/// <summary>Supplies the target and approved references for assembly graph selection.</summary>
public sealed record UnityCompilationProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Name { get; init; } = string.Empty;
    public string UnityVersion { get; init; } = string.Empty;
    public string TargetPlatform { get; init; } = string.Empty;
    public string[] Defines { get; init; } = [];
    public string[] RootAssemblies { get; init; } = [];
    public bool IncludeTestAssemblies { get; init; }
    public string SdkVersion { get; init; } = string.Empty;
    public string ReferencePackVersion { get; init; } = string.Empty;
    public string LanguageVersion { get; init; } = string.Empty;
    public UnityCompilationReference[] References { get; init; } = [];

    /// <summary>Names imported source assemblies permitted to supply exact matching DLL references.</summary>
    public string[] SourceReferenceAssemblies { get; init; } = [];

    /// <summary>Fingerprints every profile field used to select and compile source.</summary>
    public string ComputeSha256() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
}
