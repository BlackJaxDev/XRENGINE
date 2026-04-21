using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Scene.Components.Particles;

/// <summary>
/// Emitter parameters passed to the GPU.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUEmitterParams
{
    public Vector3 EmitterPosition;
    public float DeltaTime;
    public Vector3 EmitterForward;
    public float TotalTime;
    public Vector3 EmitterUp;
    public uint MaxParticles;
    public Vector3 EmitterRight;
    public uint ActiveParticles;
    public Vector3 Gravity;
    public float EmissionRate;
    public Vector4 InitialColor;
    public Vector3 InitialVelocityMin;
    public float InitialLifeMin;
    public Vector3 InitialVelocityMax;
    public float InitialLifeMax;
    public Vector3 InitialScaleMin;
    public float Padding0;
    public Vector3 InitialScaleMax;
    public float Padding1;
    public Vector4 SpawnAreaMin;
    public Vector4 SpawnAreaMax;

    public const int SizeInBytes = 192;
}
