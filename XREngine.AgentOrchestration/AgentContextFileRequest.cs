namespace XREngine.AgentOrchestration;

/// <summary>
/// Selects one repository text file to snapshot before a broker run starts.
/// </summary>
public sealed record AgentContextFileRequest
{
    /// <summary>Repository-relative path to the text file.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Optional one-based first line to include.</summary>
    public int? StartLine { get; init; }

    /// <summary>Optional inclusive one-based last line to include.</summary>
    public int? EndLine { get; init; }

    /// <summary>
    /// Optional SHA-256 of the complete raw file. Admission fails when the
    /// current file does not match this value.
    /// </summary>
    public string? ExpectedSha256 { get; init; }
}
