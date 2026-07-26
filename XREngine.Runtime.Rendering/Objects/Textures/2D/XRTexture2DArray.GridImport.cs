using ImageMagick;

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

        using MagickImage source = new(filePath);
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
                using MagickImage frame = (MagickImage)source.Clone();
                frame.Crop(new MagickGeometry(
                    column * (int)frameWidth,
                    row * (int)frameHeight,
                    frameWidth,
                    frameHeight));
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
