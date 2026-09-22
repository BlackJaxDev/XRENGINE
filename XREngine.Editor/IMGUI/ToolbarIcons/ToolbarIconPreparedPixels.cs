namespace XREngine.Editor;

internal sealed class ToolbarIconPreparedPixels(
    byte[] pixels,
    string sourcePath,
    int inputBytes,
    double pathMilliseconds,
    double readMilliseconds,
    double rasterMilliseconds)
{
    public byte[] Pixels { get; } = pixels;
    public string SourcePath { get; } = sourcePath;
    public int InputBytes { get; } = inputBytes;
    public double PathMilliseconds { get; } = pathMilliseconds;
    public double ReadMilliseconds { get; } = readMilliseconds;
    public double RasterMilliseconds { get; } = rasterMilliseconds;
}
