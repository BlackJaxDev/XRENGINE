namespace XREngine.Rendering.API.Rendering.OpenXR;

public sealed class OpenXrSmokeSwapchainSummary
{
    public int ViewIndex { get; set; }
    public string Backend { get; set; } = string.Empty;
    public uint Width { get; set; }
    public uint Height { get; set; }
    public long Format { get; set; }
    public uint SampleCount { get; set; }
    public uint ImageCount { get; set; }
}
