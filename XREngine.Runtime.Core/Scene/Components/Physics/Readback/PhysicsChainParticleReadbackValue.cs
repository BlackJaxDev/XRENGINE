using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Components;

/// <summary>
/// Packed current and previous particle positions.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PhysicsChainParticleReadbackValue(
    Vector3 CurrentPosition,
    Vector3 PreviousPosition);
