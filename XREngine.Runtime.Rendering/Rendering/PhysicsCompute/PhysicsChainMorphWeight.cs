namespace XREngine.Rendering.Compute;

/// <summary>Retains one blendshape input with a physics output page.</summary>
public readonly record struct PhysicsChainMorphWeight(uint ShapeIndex, float Weight);
