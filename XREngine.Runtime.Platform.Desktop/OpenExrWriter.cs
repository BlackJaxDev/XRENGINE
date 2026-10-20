using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace XREngine.Data.Core.Files;

/// <summary>
/// Writes uncompressed scanline OpenEXR images with four full-precision FLOAT channels.
/// This preserves diagnostic data that exceeds the range or precision of HALF channels.
/// </summary>
public static class OpenExrWriter
{
    /// <summary>
    /// Writes interleaved RGBA pixels without color conversion, clamping, or finite-value
    /// substitution. Rows are stored top to bottom; set <paramref name="flipVertically"/>
    /// when the input begins at the bottom of the image.
    /// </summary>
    public static void WriteRgbaFloat(string path, ReadOnlySpan<float> pixels, int width, int height, bool flipVertically = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int rowValues = checked(width * 4);
        int rowBytes = checked(rowValues * sizeof(float));
        if (pixels.Length != checked(rowValues * height))
            throw new ArgumentException("The pixel count must match the RGBA image dimensions.", nameof(pixels));

        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(20000630u); // OpenEXR magic, followed by version 2 with no optional flags.
        writer.Write(2u);

        WriteAttributeHeader(writer, "channels", "chlist", 73);
        foreach (char channel in "ABGR")
        {
            writer.Write((byte)channel);
            writer.Write((byte)0);
            writer.Write(2); // FLOAT, not HALF. Channel samples follow alphabetical order.
            writer.Write(0); // pLinear and three reserved bytes.
            writer.Write(1); // xSampling
            writer.Write(1); // ySampling
        }
        writer.Write((byte)0);

        WriteAttributeHeader(writer, "compression", "compression", 1);
        writer.Write((byte)0);
        WriteWindow(writer, "dataWindow", width, height);
        WriteWindow(writer, "displayWindow", width, height);
        WriteAttributeHeader(writer, "lineOrder", "lineOrder", 1);
        writer.Write((byte)0);
        WriteAttributeHeader(writer, "pixelAspectRatio", "float", 4);
        writer.Write(1.0f);
        WriteAttributeHeader(writer, "screenWindowCenter", "v2f", 8);
        writer.Write(0.0f);
        writer.Write(0.0f);
        WriteAttributeHeader(writer, "screenWindowWidth", "float", 4);
        writer.Write(1.0f);
        writer.Write((byte)0); // End of header.

        long firstChunk = checked(stream.Position + (long)height * sizeof(ulong));
        long chunkBytes = (long)rowBytes + 2 * sizeof(int);
        for (int y = 0; y < height; ++y)
            writer.Write(checked((ulong)(firstChunk + y * chunkBytes)));

        // The export is cold-path diagnostic I/O. Pool one planar scanline rather
        // than constructing another full image or scaling through image-library quanta.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(rowBytes);
        try
        {
            Span<byte> row = buffer.AsSpan(0, rowBytes);
            for (int y = 0; y < height; ++y)
            {
                int sourceY = flipVertically ? height - 1 - y : y;
                ReadOnlySpan<float> source = pixels.Slice(sourceY * rowValues, rowValues);
                for (int channel = 0; channel < 4; ++channel)
                {
                    int destination = channel * width * sizeof(float);
                    for (int x = 0; x < width; ++x)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(row.Slice(destination, sizeof(float)), source[x * 4 + 3 - channel]);
                        destination += sizeof(float);
                    }
                }
                writer.Write(y);
                writer.Write(rowBytes);
                writer.Write(row);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WriteWindow(BinaryWriter writer, string name, int width, int height)
    {
        WriteAttributeHeader(writer, name, "box2i", 16);
        writer.Write(0);
        writer.Write(0);
        writer.Write(width - 1);
        writer.Write(height - 1);
    }

    private static void WriteAttributeHeader(BinaryWriter writer, string name, string type, int size)
    {
        WriteAsciiName(writer, name);
        WriteAsciiName(writer, type);
        writer.Write(size);
    }

    private static void WriteAsciiName(BinaryWriter writer, string value)
    {
        // BinaryWriter.Write(string) uses a length prefix; EXR requires a NUL terminator.
        foreach (char character in value)
            writer.Write((byte)character);
        writer.Write((byte)0);
    }
}
