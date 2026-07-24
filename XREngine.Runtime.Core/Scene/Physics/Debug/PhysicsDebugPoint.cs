using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Packed GPU-ready point record. Color uses RGBA8 UNORM byte order.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PhysicsDebugPoint(Vector3 Position, uint Color);
