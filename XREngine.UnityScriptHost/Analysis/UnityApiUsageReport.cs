namespace XREngine.UnityScriptHost.Analysis;

/// <summary>Reports static external member references without claiming that they execute or convert.</summary>
public sealed record UnityApiUsageReport
{
    /// <summary>Gets the spelling version for catalog keys.</summary>
    public int CatalogKeyVersion { get; init; } = 1;

    /// <summary>Gets the metadata assembly name.</summary>
    public string AssemblyName { get; init; } = string.Empty;

    /// <summary>Gets the SHA-256 digest of the inspected assembly bytes.</summary>
    public string AssemblySha256 { get; init; } = string.Empty;

    /// <summary>Gets the SHA-256 digest of caller-provided catalog data, when supplied.</summary>
    public string CatalogSha256 { get; init; } = string.Empty;

    /// <summary>Gets the number of member reference rows read from metadata.</summary>
    public int MemberReferenceCount { get; init; }

    /// <summary>Gets distinct external method and field references sorted by catalog key.</summary>
    public UnityApiUsageMember[] Members { get; init; } = [];

    /// <summary>Gets limits that prevent this report from proving runtime behavior.</summary>
    public string[] Limitations { get; init; } =
    [
        "Member references are static metadata. They do not prove that a member executes.",
        "Reflection, dynamic calls, type-only references, and source not compiled into this assembly are not covered.",
        "Assembly names in catalog keys are simple names from this PE. Exact input and catalog hashes identify the inspected versions.",
        "Raw assembly, namespace, type, and member name characters outside ASCII letters, digits, underscore, dollar, and backtick use %HHHH UTF-16 escaping in catalog keys.",
        "Catalog status describes authoring API support only. It does not prove native conversion support."
    ];
}
