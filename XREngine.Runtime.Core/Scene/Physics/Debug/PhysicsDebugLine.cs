using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Packed GPU-ready line record. Color uses RGBA8 UNORM byte order.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PhysicsDebugLine(Vector3 Start, Vector3 End, uint Color);
