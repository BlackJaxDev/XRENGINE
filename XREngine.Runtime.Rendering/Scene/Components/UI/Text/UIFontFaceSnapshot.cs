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
        ContentIdentity = Convert.ToHexString(SHA256.HashData(_fontBytes));
        Glyphs = glyphs.ToFrozenDictionary();
    }

    public string ContentIdentity { get; }
    public int FaceIndex { get; }
    public float EmSize { get; }
    public FrozenDictionary<uint, UIRuneMetrics> Glyphs { get; }

    /// <summary>Give a native adapter its own copy of the face bytes.</summary>
    public byte[] CopyFontBytes() => (byte[])_fontBytes.Clone();
}
