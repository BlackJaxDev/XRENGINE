using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Stores one RGBA16_UNORM editor identity pixel as four exact channel codes.
/// The retained snapshot lease supplies the package generation during decode.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public readonly struct EditorPickingEncodedPixel
{
    public readonly ushort KeyLow;
    public readonly ushort KeyHigh;
    public readonly ushort SourceKind;
    public readonly ushort Coverage;

    public EditorPickingEncodedPixel(
        ushort keyLow,
        ushort keyHigh,
        ushort sourceKind,
        ushort coverage)
    {
        KeyLow = keyLow;
        KeyHigh = keyHigh;
        SourceKind = sourceKind;
        Coverage = coverage;
    }

    public static EditorPickingEncodedPixel Encode(
        uint sourceKey,
        EditorPickingSourceKind sourceKind,
        ushort coverage)
        => new(
            unchecked((ushort)sourceKey),
            unchecked((ushort)(sourceKey >> 16)),
            (ushort)sourceKind,
            coverage);

    public bool TryDecode(out uint sourceKey, out EditorPickingSourceKind sourceKind)
    {
        sourceKey = (uint)KeyLow | ((uint)KeyHigh << 16);
        sourceKind = (EditorPickingSourceKind)SourceKind;
        if (Coverage == 0)
            return false;

        return sourceKind switch
        {
            EditorPickingSourceKind.NativeDraw => sourceKey is not (0u or uint.MaxValue),
            EditorPickingSourceKind.AuthoredCommand => sourceKey != 0u,
            _ => false,
        };
    }
}
