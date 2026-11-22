using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>Inline, bit-preserving image of the canonical Uber numeric block (<see cref="UberBaseParameterSchema.ByteSize"/> bytes).</summary>
[InlineArray(UberBaseParameterSchema.ByteSize / sizeof(uint))]
public struct AdvancedUberBaseParameterWords
{
    private uint _element0;
}
