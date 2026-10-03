using XREngine.Imaging;

namespace XREngine.Rendering;

public partial class XRTexture2DArray
{
    /// <summary>
    /// Loads a row-major sprite grid as a native texture array.
    /// </summary>
    public static XRTexture2DArray LoadGrid(string filePath, int rows, int columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);

        using RuntimeImage source = RuntimeImageCodecs.Require().Decode(File.ReadAllBytes(filePath));
        if (source.Width % columns != 0 || source.Height % rows != 0)
            throw new InvalidDataException(
                $"Texture dimensions {source.Width}x{source.Height} are not divisible by the {columns}x{rows} flipbook grid.");

        uint frameWidth = source.Width / (uint)columns;
        uint frameHeight = source.Height / (uint)rows;
        XRTexture2D[] frames = new XRTexture2D[checked(rows * columns)];

        for (int row = 0; row < rows; ++row)
        {
            for (int column = 0; column < columns; ++column)
            {
                int bytesPerPixel = RuntimeImage.GetBytesPerPixel(source.Format, source.Type);
                int frameRowBytes = checked((int)frameWidth * bytesPerPixel);
                byte[] framePixels = new byte[checked(frameRowBytes * (int)frameHeight)];
                ReadOnlySpan<byte> sourcePixels = source.Pixels.Span;
                for (int frameRow = 0; frameRow < frameHeight; frameRow++)
                {
                    int sourceRow = row * (int)frameHeight + frameRow;
                    if (source.Origin == RuntimeImageOrigin.BottomLeft)
                        sourceRow = checked((int)source.Height - sourceRow - 1);
                    int sourceOffset = checked(sourceRow * source.RowStrideBytes + column * frameRowBytes);
                    sourcePixels.Slice(sourceOffset, frameRowBytes)
                        .CopyTo(framePixels.AsSpan(frameRow * frameRowBytes, frameRowBytes));
                }
                using RuntimeImage frame = new(frameWidth, frameHeight, source.Format, source.Type, framePixels);
                frames[row * columns + column] = new XRTexture2D(frame);
            }
        }

        return new XRTexture2DArray(frames)
        {
            AutoGenerateMipmaps = true,
            Resizable = false,
        };
    }
}
