namespace XREngine.Browser;

/// <summary>Describes concatenated, offline encoded texture mip payloads.</summary>
internal sealed class BrowserCookedTextureDto
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Format { get; init; }
    public required int[] MipByteLengths { get; init; }
    public required string NormalConvention { get; init; }
    public required string AlphaMode { get; init; }
}
