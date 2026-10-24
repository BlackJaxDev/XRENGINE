using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Security.Cryptography;

namespace XREngine.Rendering.UI;

/// <summary>Owned face data and atlas metrics for optional glyph-ID shaping.</summary>
public sealed class UIFontFaceSnapshot
{
    private const int MaxFaceBytes = 16 * 1024 * 1024;
    private readonly byte[] _fontBytes;

    public UIFontFaceSnapshot(byte[] fontBytes, int faceIndex, float emSize,
        IReadOnlyDictionary<uint, UIRuneMetrics> glyphs)
    {
        ArgumentNullException.ThrowIfNull(fontBytes);
        ArgumentNullException.ThrowIfNull(glyphs);
        if (fontBytes.Length == 0 || fontBytes.Length > MaxFaceBytes || faceIndex < 0 ||
            !float.IsFinite(emSize) || emSize <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(fontBytes), "Invalid shaping face data.");
        _fontBytes = (byte[])fontBytes.Clone();
        FaceIndex = faceIndex;
        EmSize = emSize;
        Glyphs = glyphs.ToFrozenDictionary();
        ContentIdentity = CreateContentIdentity();
    }

    public string ContentIdentity { get; }
    public int FaceIndex { get; }
    public float EmSize { get; }
    public FrozenDictionary<uint, UIRuneMetrics> Glyphs { get; }
    public int ByteLength => _fontBytes.Length;

    /// <summary>Give a native adapter its own copy of the face bytes.</summary>
    public byte[] CopyFontBytes() => (byte[])_fontBytes.Clone();

    private string CreateContentIdentity()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header, _fontBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], FaceIndex);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], BitConverter.SingleToInt32Bits(EmSize));
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], Glyphs.Count);
        hash.AppendData(header);
        hash.AppendData(_fontBytes);

        uint[] glyphIds = [.. Glyphs.Keys];
        Array.Sort(glyphIds);
        Span<byte> glyphData = stackalloc byte[40];
        foreach (uint glyphId in glyphIds)
        {
            UIRuneMetrics metric = Glyphs[glyphId];
            BinaryPrimitives.WriteUInt32LittleEndian(glyphData, glyphId);
            WriteFloat(glyphData, 4, metric.Bearing.X);
            WriteFloat(glyphData, 8, metric.Bearing.Y);
            WriteFloat(glyphData, 12, metric.Size.X);
            WriteFloat(glyphData, 16, metric.Size.Y);
            WriteFloat(glyphData, 20, metric.Advance);
            WriteFloat(glyphData, 24, metric.AtlasRect.X);
            WriteFloat(glyphData, 28, metric.AtlasRect.Y);
            WriteFloat(glyphData, 32, metric.AtlasRect.Z);
            WriteFloat(glyphData, 36, metric.AtlasRect.W);
            hash.AppendData(glyphData);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void WriteFloat(Span<byte> destination, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], BitConverter.SingleToInt32Bits(value));
}
