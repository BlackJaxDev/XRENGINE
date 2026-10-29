using System.Buffers.Binary;
using XREngine.Imaging;
using XREngine.Rendering;

namespace XREngine.Editor;

/// <summary>Imports bounded static PNG or JPEG bytes without copying the source into the project.</summary>
internal static class NativeTextureFileImport
{
    internal const int MaximumSourceBytes = 32 * 1024 * 1024;
    internal const uint MaximumDimension = 8192;
    internal const long MaximumPixels = 4L * 1024 * 1024;
    internal const long MaximumAssetBytes = 64L * 1024 * 1024;

    internal static XRTexture2D Read(string sourcePath, string assetsRoot, Func<bool> admission)
    {
        Require(admission);
        string source = Path.GetFullPath(sourcePath);
        if ((File.GetAttributes(source) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The dropped image must be an ordinary file.");
        using FileStream stream = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumSourceBytes)
            throw new InvalidDataException("The dropped image exceeds the encoded byte limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new IOException("The dropped image changed while it was read.");
        Require(admission);
        return Decode(bytes, Path.GetFileNameWithoutExtension(source), assetsRoot, admission);
    }

    internal static XRTexture2D Decode(byte[] bytes, string name, string assetsRoot, Func<bool> admission)
    {
        Require(admission);
        (uint width, uint height) = Inspect(bytes);
        using RuntimeImage image = RuntimeImageCodecs.Require().Decode(bytes);
        Require(admission);
        if (image.Width != width || image.Height != height || image.Pixels.Length > MaximumPixels * 16)
            throw new InvalidDataException("Decoded image dimensions or storage differ from the admitted header.");
        string root = Path.GetFullPath(assetsRoot);
        string safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "Dropped texture";
        string destination = Path.Combine(root, safeName + ".asset");
        for (int suffix = 1; File.Exists(destination) || Directory.Exists(destination); suffix++)
        {
            if (suffix > 10000) throw new IOException("No unused texture asset name is available.");
            destination = Path.Combine(root, $"{safeName} ({suffix}).asset");
        }
        XRTexture2D texture = new();
        try
        {
            texture.Name = safeName;
            texture.Mipmaps = [new Mipmap2D(image)];
            texture.SizedInternalFormat = XRTexture2D.DeriveESizedInternalFormat(texture.Mipmaps[0].InternalFormat);
            texture.AutoGenerateMipmaps = true;
            texture.Resizable = false;
            texture.FilePath = destination;
            Require(admission);
            return texture;
        }
        catch
        {
            texture.Destroy(true);
            throw;
        }
    }

    internal static (uint Width, uint Height) Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumSourceBytes)
            throw new InvalidDataException("The dropped image exceeds the encoded byte limit.");
        uint width, height;
        if (bytes.Length >= 33 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
                throw new InvalidDataException("The PNG header is invalid.");
            width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
            height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
            byte depth = bytes[24], color = bytes[25];
            bool validDepth = color switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => depth is 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => false };
            if (!validDepth || bytes[26] != 0 || bytes[27] != 0 || bytes[28] > 1)
                throw new InvalidDataException("The PNG pixel profile is unsupported.");
            int offset = 8;
            bool ended = false, hasPixels = false;
            while (offset <= bytes.Length - 12)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
                if (length > bytes.Length - offset - 12) throw new InvalidDataException("The PNG chunk is truncated.");
                ReadOnlySpan<byte> kind = bytes.Slice(offset + 4, 4);
                if (kind.SequenceEqual("acTL"u8)) throw new NotSupportedException("Animated PNG drops are unsupported.");
                if (kind.SequenceEqual("IHDR"u8) && offset != 8)
                    throw new InvalidDataException("The PNG has more than one image header.");
                if (kind.SequenceEqual("IDAT"u8)) hasPixels = true;
                else if (!kind.SequenceEqual("IHDR"u8) && !kind.SequenceEqual("IEND"u8) &&
                    !kind.SequenceEqual("PLTE"u8) && !kind.SequenceEqual("tRNS"u8) &&
                    !kind.SequenceEqual("gAMA"u8) && !kind.SequenceEqual("cHRM"u8) &&
                    !kind.SequenceEqual("sRGB"u8) && !kind.SequenceEqual("pHYs"u8) && !kind.SequenceEqual("sBIT"u8) &&
                    !kind.SequenceEqual("tEXt"u8) && !kind.SequenceEqual("tIME"u8) && !kind.SequenceEqual("bKGD"u8))
                    throw new NotSupportedException("The PNG contains unsupported metadata or image chunks.");
                offset += checked((int)length + 12);
                if (kind.SequenceEqual("IEND"u8)) { ended = length == 0 && offset == bytes.Length; break; }
            }
            if (!ended || !hasPixels) throw new InvalidDataException("The PNG stream is incomplete.");
        }
        else if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            width = height = 0;
            int offset = 2;
            bool entropy = false, scanned = false, ended = false;
            while (offset < bytes.Length)
            {
                if (entropy)
                {
                    while (offset < bytes.Length && bytes[offset] != 0xff) offset++;
                    if (offset >= bytes.Length) break;
                }
                if (bytes[offset++] != 0xff) throw new InvalidDataException("The JPEG marker is invalid.");
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset >= bytes.Length) throw new InvalidDataException("The JPEG marker is truncated.");
                byte marker = bytes[offset++];
                if (entropy && (marker == 0x00 || marker is >= 0xd0 and <= 0xd7)) continue;
                entropy = false;
                if (marker == 0xd9) { ended = scanned && offset == bytes.Length; break; }
                if (marker is 0x00 or 0xd8 or >= 0xd0 and <= 0xd7) throw new InvalidDataException("The JPEG header marker is invalid.");
                if (offset > bytes.Length - 2) throw new InvalidDataException("The JPEG segment is truncated.");
                int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
                if (length < 2 || length > bytes.Length - offset) throw new InvalidDataException("The JPEG segment is truncated.");
                if (marker is 0xc0 or 0xc1 or 0xc2)
                {
                    if (length < 8 || bytes[offset + 2] != 8 || bytes[offset + 7] is not (1 or 3) ||
                        length != 8 + 3 * bytes[offset + 7] || width != 0)
                        throw new NotSupportedException("The JPEG pixel profile is unsupported.");
                    height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]);
                }
                else if ((marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xcc)) || marker is 0xdc or 0xde or 0xdf)
                    throw new NotSupportedException("The JPEG frame encoding is unsupported.");
                else if (marker == 0xda)
                {
                    if (width == 0 || length < 6 || bytes[offset + 2] is not (1 or 2 or 3) ||
                        length != 6 + 2 * bytes[offset + 2])
                        throw new InvalidDataException("The JPEG scan header is invalid.");
                    entropy = scanned = true;
                }
                offset += length;
            }
            if (width == 0 || height == 0 || !ended)
                throw new InvalidDataException("The JPEG stream has no complete admitted frame.");
        }
        else throw new NotSupportedException("Native texture drops support static PNG and JPEG images only.");
        if (width == 0 || height == 0 || width > MaximumDimension || height > MaximumDimension || (long)width * height > MaximumPixels)
            throw new InvalidDataException("The image exceeds the decoded pixel limit.");
        return (width, height);
    }

    private static void Require(Func<bool> admission)
    {
        if (!admission()) throw new InvalidOperationException("The texture drop target changed.");
    }
}
