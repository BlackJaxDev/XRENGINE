namespace XREngine.Data;

/// <summary>Contains the exit status and captured output of a completed external tool.</summary>
public sealed record RuntimeProcessResult(int ExitCode, string StandardOutput, string StandardError);
