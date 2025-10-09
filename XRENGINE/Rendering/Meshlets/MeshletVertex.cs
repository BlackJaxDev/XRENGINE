using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Objects;

namespace XREngine.Rendering.Meshlets
{
    ///// <summary>
    ///// Vertex data for meshlet rendering
    ///// </summary>
    //[StructLayout(LayoutKind.Sequential, Pack = 1)]
    //public struct MeshletVertex : IBufferable
    //{
    //    public Vector3 Position;
    //    public Vector3 Normal;
    //    public Vector2 TexCoord;
    //    public Vector3 Tangent;

    //    public const int SizeInBytes = 48;

    //    public MeshletVertex()
    //    {
    //        Position = Vector3.Zero;
    //        Normal = Vector3.UnitY;
    //        TexCoord = Vector2.Zero;
    //        Tangent = Vector3.UnitX;
    //    }

    //    public EComponentType ComponentType { get; } = EComponentType.Struct;
    //    public uint ComponentCount { get; } = 1;
    //    public bool Normalize { get; } = false;

    //    public unsafe void Read(VoidPtr address)
    //        => this = *(MeshletVertex*)address.Pointer;
    //    public readonly unsafe void Write(VoidPtr address)
    //        => *(MeshletVertex*)address.Pointer = this;
    //}
}
