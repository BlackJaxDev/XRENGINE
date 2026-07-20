using ImageMagick;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Builds a bounded row-major contact sheet from successfully encoded sequence frames.
/// </summary>
internal static class ViewportSequenceCaptureContactSheetWriter
{
    private const int GutterPixels = 4;

    public static bool TryWrite(
        IReadOnlyList<ViewportSequenceCaptureFrame> frames,
        string outputPath,
        int requestedColumns,
        int requestedThumbnailWidth,
        out string? error)
    {
        error = null;
        ViewportSequenceCaptureFrame[] successfulFrames = frames
            .Where(static frame => frame.Succeeded && File.Exists(frame.Path))
            .OrderBy(static frame => frame.CaptureIndex)
            .ToArray();

        if (successfulFrames.Length == 0)
        {
            error = "No successfully encoded frames were available for the contact sheet.";
            return false;
        }

        try
        {
            using MagickImage first = new(successfulFrames[0].Path);
            int columns = requestedColumns > 0
                ? Math.Min(requestedColumns, successfulFrames.Length)
                : Math.Max(1, (int)Math.Ceiling(Math.Sqrt(successfulFrames.Length)));
            int rows = (successfulFrames.Length + columns - 1) / columns;
            int thumbnailWidth = requestedThumbnailWidth;
            int thumbnailHeight = CalculateHeight(thumbnailWidth, first.Width, first.Height);

            ReduceToPixelBudget(columns, rows, ref thumbnailWidth, ref thumbnailHeight);

            int cellWidth = thumbnailWidth + GutterPixels * 2;
            int cellHeight = thumbnailHeight + GutterPixels * 2;
            uint sheetWidth = checked((uint)(columns * cellWidth));
            uint sheetHeight = checked((uint)(rows * cellHeight));

            using MagickImage sheet = new(MagickColors.Black, sheetWidth, sheetHeight);
            for (int i = 0; i < successfulFrames.Length; i++)
            {
                ViewportSequenceCaptureFrame frame = successfulFrames[i];
                int row = i / columns;
                int column = i % columns;
                frame.ContactSheetRow = row;
                frame.ContactSheetColumn = column;

                using MagickImage thumbnail = new(frame.Path);
                CalculateContainedSize(thumbnail.Width, thumbnail.Height, thumbnailWidth, thumbnailHeight, out uint width, out uint height);
                thumbnail.Resize(width, height);

                int x = column * cellWidth + GutterPixels + (thumbnailWidth - (int)width) / 2;
                int y = row * cellHeight + GutterPixels + (thumbnailHeight - (int)height) / 2;
                sheet.Composite(thumbnail, x, y, CompositeOperator.Over);
            }

            sheet.Write(outputPath, MagickFormat.Png);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static int CalculateHeight(int width, uint sourceWidth, uint sourceHeight)
    {
        if (sourceWidth == 0 || sourceHeight == 0)
            return width;

        double aspect = sourceHeight / (double)sourceWidth;
        return Math.Clamp((int)Math.Round(width * aspect), 64, width * 2);
    }

    private static void ReduceToPixelBudget(int columns, int rows, ref int width, ref int height)
    {
        long requestedPixels = (long)columns * (width + GutterPixels * 2) * rows * (height + GutterPixels * 2);
        if (requestedPixels <= ViewportSequenceCaptureOptions.MaximumContactSheetPixels)
            return;

        double scale = Math.Sqrt(ViewportSequenceCaptureOptions.MaximumContactSheetPixels / (double)requestedPixels);
        width = Math.Max(64, (int)Math.Floor(width * scale));
        height = Math.Max(64, (int)Math.Floor(height * scale));
    }

    private static void CalculateContainedSize(
        uint sourceWidth,
        uint sourceHeight,
        int maximumWidth,
        int maximumHeight,
        out uint width,
        out uint height)
    {
        if (sourceWidth == 0 || sourceHeight == 0)
        {
            width = (uint)maximumWidth;
            height = (uint)maximumHeight;
            return;
        }

        double scale = Math.Min(maximumWidth / (double)sourceWidth, maximumHeight / (double)sourceHeight);
        width = (uint)Math.Max(1, Math.Round(sourceWidth * scale));
        height = (uint)Math.Max(1, Math.Round(sourceHeight * scale));
    }
}
