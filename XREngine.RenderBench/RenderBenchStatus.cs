namespace XREngine.RenderBench;

public sealed record RenderBenchStatus(
    RenderBenchPhase Phase,
    int ProcessId,
    string? SessionName,
    string Backend,
    string ExecutionMode,
    string Recipe,
    string OutputDirectory,
    string? ResultPath,
    string? Failure);
