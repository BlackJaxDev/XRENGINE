using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// CPU diagnostic/reference vertex for deterministic deformation tests.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedReferenceVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector3 Tangent);
