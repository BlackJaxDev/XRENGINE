using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Objects;

namespace XREngine.Rendering.Meshlets;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MeshletVertex() : IBufferable
{
    public Vector4 Position;
    public Vector4 Normal;
    public Vector2 TexCoord;
    public Vector2 Padding;
    public Vector4 Tangent;

    public EComponentType ComponentType { get; } = EComponentType.Struct;
    public uint ComponentCount { get; } = 1;
    public bool Normalize { get; } = false;

    public unsafe void Read(VoidPtr address)
        => this = *(MeshletVertex*)address.Pointer;

    public readonly unsafe void Write(VoidPtr address)
        => *(MeshletVertex*)address.Pointer = this;
}