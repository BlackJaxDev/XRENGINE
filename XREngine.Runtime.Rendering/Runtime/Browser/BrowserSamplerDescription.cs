using System.Globalization;

namespace XREngine.Rendering;

/// <summary>Immutable 2D sampling policy for resource samplers and browser materials.</summary>
public sealed record BrowserSamplerDescription
{
    public BrowserSamplerDescription(string AddressU = "clamp-to-edge", string AddressV = "clamp-to-edge",
        string MinFilter = "linear", string MagFilter = "linear", string MipmapFilter = "linear",
        string Label = "", float LodMaxClamp = 32, int MaxAnisotropy = 1)
    {
        if (!ValidAddress(AddressU) || !ValidAddress(AddressV) ||
            !ValidFilter(MinFilter) || !ValidFilter(MagFilter) || !ValidFilter(MipmapFilter) ||
            !float.IsFinite(LodMaxClamp) || LodMaxClamp is < 0 or > 32 ||
            MaxAnisotropy is < 1 or > 16 ||
            (MaxAnisotropy > 1 && (MinFilter != "linear" || MagFilter != "linear" || MipmapFilter != "linear")))
            throw new ArgumentException("Browser sampler address, filtering, LOD or anisotropy policy is unsupported.");
        this.AddressU = AddressU;
        this.AddressV = AddressV;
        this.MinFilter = MinFilter;
        this.MagFilter = MagFilter;
        this.MipmapFilter = MipmapFilter;
        this.Label = Label;
        this.LodMaxClamp = LodMaxClamp;
        this.MaxAnisotropy = MaxAnisotropy;
    }

    public string AddressU { get; }
    public string AddressV { get; }
    public string MinFilter { get; }
    public string MagFilter { get; }
    public string MipmapFilter { get; }
    public string Label { get; }
    public float LodMaxClamp { get; }
    public int MaxAnisotropy { get; }

    internal string ToPipelineJson() =>
        $"{{\"addressModeU\":\"{AddressU}\",\"addressModeV\":\"{AddressV}\",\"minFilter\":\"{MinFilter}\",\"magFilter\":\"{MagFilter}\",\"mipmapFilter\":\"{MipmapFilter}\",\"lodMaxClamp\":{LodMaxClamp.ToString("R", CultureInfo.InvariantCulture)},\"maxAnisotropy\":{MaxAnisotropy}}}";

    private static bool ValidAddress(string value) => value is "clamp-to-edge" or "repeat" or "mirror-repeat";
    private static bool ValidFilter(string value) => value is "nearest" or "linear";
}
