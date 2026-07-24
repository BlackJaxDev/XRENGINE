using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Components;

/// <summary>
/// Packed axis-aligned bounds.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PhysicsChainBoundsReadbackValue(
    Vector3 Minimum,
    Vector3 Maximum);
