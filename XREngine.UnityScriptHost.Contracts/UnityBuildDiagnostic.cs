namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Describes one failure or observation with optional source context.
/// </summary>
public sealed record UnityBuildDiagnostic
{
    /// <summary>Gets the diagnostic class.</summary>
    public UnityDiagnosticCode Code { get; init; }

    /// <summary>Gets the diagnostic severity.</summary>
    public UnityDiagnosticSeverity Severity { get; init; }

    /// <summary>Gets the diagnostic text.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the assembly identity, when known.</summary>
    public string? Assembly { get; init; }

    /// <summary>Gets the type identity, when known.</summary>
    public string? Type { get; init; }

    /// <summary>Gets the exact member identifier, when known.</summary>
    public string? Member { get; init; }

    /// <summary>Gets the callback identity, when known.</summary>
    public string? Callback { get; init; }

    /// <summary>Gets the source object identity, when known.</summary>
    public string? SourceObjectId { get; init; }

    /// <summary>Gets the source location or stack, when known.</summary>
    public string? LocationOrStack { get; init; }
}
