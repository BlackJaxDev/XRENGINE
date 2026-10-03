using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>The frozen binding and current contribution of a calibrated slot.</summary>
public readonly record struct VrCalibrationSlotState(
    TransformBase? Device,
    TransformBase Target,
    Matrix4x4 DeviceToTargetOffset,
    string? Identity,
    float Weight,
    EVrCalibrationSource Source);
