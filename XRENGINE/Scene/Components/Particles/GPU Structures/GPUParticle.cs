using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene.Components.Particles;

/// <summary>
/// GPU-side particle data structure.
/// Must match the shader struct layout exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUParticle
{
    public Vector3 Position;
    public float Life;
    public Vector3 Velocity;
    public float MaxLife;
    public Vector4 Color;
    public Vector3 Scale;
    public float Rotation;
    public Vector3 AngularVelocity;
    public uint Flags;
    public Vector4 CustomData0;
    public Vector4 CustomData1;

    public const int SizeInBytes = 96; // 24 floats * 4 bytes
    public const int SizeInFloats = 24;
}
