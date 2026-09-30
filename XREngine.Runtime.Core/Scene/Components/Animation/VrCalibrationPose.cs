using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>A source world pose copied from one immutable tracking publication.</summary>
public readonly record struct VrCalibrationPose(TransformBase Source, Matrix4x4 WorldPose);
