using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Allocation-free snapshot of native body-frame derivation and Hips compensation.
/// A default value has <see cref="HasValue"/> set to <see langword="false"/>.
/// </summary>
public readonly record struct HumanoidBodyFrameDiagnosticState(
    bool HasValue,
    string ModelId,
    int AlgorithmVersion,
    Matrix4x4 ProvisionalBodyFrame,
    Matrix4x4 RequestedBodyBeforeProjection,
    Matrix4x4 RequestedBodyFrame,
    Matrix4x4 CompensatedBodyFrame,
    Matrix4x4 Compensation,
    Matrix4x4 FinalHipsLocal,
    Matrix4x4 FinalHipsModelRoot,
    HumanoidProjectedRootPose ProjectedRoot,
    HumanoidProjectedRootPose RootMotionInputPose,
    float RootMotionInputWeight,
    bool HasRootMotionInput);
