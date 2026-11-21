using System.Text.Json.Serialization;
using XREngine.UnityScriptHost.Execution;

namespace XREngine.UnityScriptHost.Analysis;

/// <summary>Identifies one external method or field reference in compiled metadata.</summary>
public sealed record UnityApiUsageMember
{
    /// <summary>Gets the escaped declaring assembly token, or an unresolved module marker.</summary>
    public string AssemblyName { get; init; } = string.Empty;

    /// <summary>Gets the canonical declaring type with escaped metadata name tokens.</summary>
    public string TypeName { get; init; } = string.Empty;

    /// <summary>Gets the member name as stored in metadata.</summary>
    public string MemberName { get; init; } = string.Empty;

    /// <summary>Gets Method or Field.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets an assembly-qualified signature that distinguishes overloads.</summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>Gets the versioned key used for catalog lookup.</summary>
    public string CatalogKey { get; init; } = string.Empty;

    /// <summary>Gets the declared catalog status. Unsupported can mean no catalog entry.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<UnityCapabilityStatus>))]
    public UnityCapabilityStatus Status { get; init; }

    /// <summary>Gets whether the supplied catalog explicitly names this exact member.</summary>
    public bool CatalogEntryPresent { get; init; }

    /// <summary>Gets whether metadata names a declaring assembly and type.</summary>
    public bool DeclaringTypeResolved { get; init; }

    /// <summary>Gets whether every type in the member signature has a known owner or a symbolic generic parameter.</summary>
    public bool SignatureResolved { get; init; }
}
