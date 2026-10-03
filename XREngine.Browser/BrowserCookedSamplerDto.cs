namespace XREngine.Browser;

/// <summary>Optional texture sampling policy on a cooked material.</summary>
internal sealed class BrowserCookedSamplerDto
{
    public required string AddressModeU { get; init; }
    public required string AddressModeV { get; init; }
    public required string MinFilter { get; init; }
    public required string MagFilter { get; init; }
    public required string MipmapFilter { get; init; }
    public required float LodMaxClamp { get; init; }
    public required int MaxAnisotropy { get; init; }
}
