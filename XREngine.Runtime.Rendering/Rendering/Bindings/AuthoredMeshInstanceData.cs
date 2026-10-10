using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>One native instance-index row: current/previous pre-model transforms and a current pre-instance bounding sphere.</summary>
/// <remarks>
/// Matrices use the engine row-vector convention. Their raw rows become WGSL columns,
/// exactly like engine matrix uniforms. Bounds cover the final vertex positions before
/// the instance transform, including every authored displacement and deformation.
/// A negative radius declares an unbounded instance.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AuthoredMeshInstanceData(
    Matrix4x4 CurrentTransform,
    Matrix4x4 PreviousTransform,
    Vector4 PreInstanceBounds);
