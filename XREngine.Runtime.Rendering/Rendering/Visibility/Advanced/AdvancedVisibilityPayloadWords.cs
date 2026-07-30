using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// The two exact unsigned words stored in the <c>RG32_UINT</c> identity attachment.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedVisibilityPayloadWords(
    uint DrawTableIndex,
    uint PrimitiveIndex)
{
    public static AdvancedVisibilityPayloadWords Invalid
        => new(
            AdvancedVisibilityBufferContract.InvalidWord,
            AdvancedVisibilityBufferContract.InvalidWord);

    public bool IsValid
        => DrawTableIndex != 0u &&
           DrawTableIndex != AdvancedVisibilityBufferContract.InvalidWord &&
           PrimitiveIndex != AdvancedVisibilityBufferContract.InvalidWord;
}
