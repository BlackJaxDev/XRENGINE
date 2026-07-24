namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Hard primitive limits applied while extracting a backend debug frame.
/// </summary>
public readonly record struct PhysicsDebugBudget(
    int MaxPoints,
    int MaxLines,
    int MaxTriangles,
    int MaxBytes)
{
    public static PhysicsDebugBudget Default { get; } = new(
        MaxPoints: 131_072,
        MaxLines: 1_048_576,
        MaxTriangles: 524_288,
        MaxBytes: 64 * 1024 * 1024);

    public PhysicsDebugBudget ClampNonNegative()
        => new(
            Math.Max(0, MaxPoints),
            Math.Max(0, MaxLines),
            Math.Max(0, MaxTriangles),
            Math.Max(0, MaxBytes));
}
