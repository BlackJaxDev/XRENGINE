using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>Exact authored local-vertex inputs copied into a retained material publication.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public readonly record struct AdvancedNativeVertexInputs(Vector4 Input0, Vector4 Input1, Vector4 Input2, Vector4 Input3)
{
    public bool IsFinite => Finite(Input0) && Finite(Input1) && Finite(Input2) && Finite(Input3);

    private static bool Finite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
