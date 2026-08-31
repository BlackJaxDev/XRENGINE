using System.Numerics;

namespace XREngine.RenderBench;

/// <summary>Caller-owned transform state for one stable production-scene candidate.</summary>
public readonly record struct RenderBenchProductionSceneCandidatePose(
    int Id,
    Vector3 Position,
    Vector3 Scale);
