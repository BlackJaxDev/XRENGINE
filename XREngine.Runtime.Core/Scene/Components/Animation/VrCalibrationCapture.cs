using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>A device pose sampled with the other devices used for one calibration capture.</summary>
public readonly record struct VrCalibrationCapture(
    TransformBase? Device,
    Matrix4x4 DeviceWorld,
    string? Identity = null,
    long SnapshotId = 0,
    long SampleTime = 0);
