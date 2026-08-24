namespace XREngine.RenderBench;

public sealed record RenderBenchGateResult(
    string Name,
    bool Passed,
    string Requirement,
    string Observed);
