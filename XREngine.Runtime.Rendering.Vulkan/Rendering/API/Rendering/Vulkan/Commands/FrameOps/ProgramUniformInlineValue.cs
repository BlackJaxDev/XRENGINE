using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Vulkan;

/// <summary>Overlays reference-free numeric uniform values in one inline slot.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct ProgramUniformInlineValue
{
    [FieldOffset(0)] internal float Float;
    [FieldOffset(0)] internal int Int;
    [FieldOffset(0)] internal uint UInt;
    [FieldOffset(0)] internal double Double;
    [FieldOffset(0)] internal Vector2 Vector2;
    [FieldOffset(0)] internal Vector3 Vector3;
    [FieldOffset(0)] internal Vector4 Vector4;
    [FieldOffset(0)] internal Matrix4x4 Matrix4x4;
    [FieldOffset(0)] internal DVector4 DVector4;
    [FieldOffset(0)] internal IVector4 IVector4;
    [FieldOffset(0)] internal UVector4 UVector4;
}
