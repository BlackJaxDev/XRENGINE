using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>Inline, bit-preserving image of the canonical 352-byte Uber numeric block.</summary>
[InlineArray(UberBaseParameterSchema.ByteSize / sizeof(uint))]
public struct AdvancedUberBaseParameterWords
{
    private uint _element0;
}
