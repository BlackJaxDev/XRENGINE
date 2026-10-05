using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>A validated proposed source and its sole device-to-target offset.</summary>
public readonly record struct VrCalibrationTarget(EHumanoidIKTarget Slot, TransformBase Source, Matrix4x4 Offset);
