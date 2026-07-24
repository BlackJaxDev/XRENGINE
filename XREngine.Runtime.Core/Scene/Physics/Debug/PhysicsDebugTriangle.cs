using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Packed GPU-ready triangle record. Color uses RGBA8 UNORM byte order.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PhysicsDebugTriangle(Vector3 A, Vector3 B, Vector3 C, uint Color);
