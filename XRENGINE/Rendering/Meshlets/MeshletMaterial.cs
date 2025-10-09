using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Objects;

namespace XREngine.Rendering.Meshlets
{
    /// <summary>
    /// Material data for meshlet rendering
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MeshletMaterial : IBufferable
    {
        public Vector4 Albedo;
        public float Metallic;
        public float Roughness;
        public float AO;
        public uint DiffuseTextureID;
        public uint NormalTextureID;
        public uint MetallicRoughnessTextureID;
        public uint Padding1;
        public uint Padding2;

        public const int SizeInBytes = 48;

        public MeshletMaterial()
        {
            Albedo = Vector4.One;
            Metallic = 0f;
            Roughness = 1f;
            AO = 1f;
            DiffuseTextureID = 0;
            NormalTextureID = 0;
            MetallicRoughnessTextureID = 0;
            Padding1 = 0;
            Padding2 = 0;
        }

        public EComponentType ComponentType { get; } = EComponentType.Struct;
        public uint ComponentCount { get; } = 1;
        public bool Normalize { get; } = false;

        public unsafe void Read(VoidPtr address)
            => this = *(MeshletMaterial*)address.Pointer;
        public readonly unsafe void Write(VoidPtr address)
            => *(MeshletMaterial*)address.Pointer = this;
    }
}
