namespace XREngine.Rendering;

public readonly record struct BlendshapeLodTier(
    BlendshapeLodEvaluation Evaluation,
    IReadOnlyList<int>? ShapeIndices = null,
    IReadOnlyList<string>? ProtectedShapeNames = null,
    float MaxDistance = 0.0f,
    float MinScreenCoverage = 0.0f);
