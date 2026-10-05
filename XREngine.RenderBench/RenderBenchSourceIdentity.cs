namespace XREngine.RenderBench;

/// <summary>Reproducible source and binary identity of a benchmark process.</summary>
public sealed record RenderBenchSourceIdentity(
    string Commit,
    bool DirtyWorktree,
    string ExecutableSha256,
    string BuildConfiguration,
    string BackendModuleGeneration);
