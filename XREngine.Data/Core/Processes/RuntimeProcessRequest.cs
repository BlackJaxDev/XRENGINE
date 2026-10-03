namespace XREngine.Data;

/// <summary>Describes an external tool invocation without exposing operating-system process handles.</summary>
public sealed record RuntimeProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null);
