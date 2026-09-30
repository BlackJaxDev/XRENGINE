using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>A currently usable source offered by the new rig, correlated by exact provider-scoped physical identity.</summary>
public readonly record struct VrCalibrationSessionSource(string PhysicalIdentity, TransformBase? Source, bool Usable);
