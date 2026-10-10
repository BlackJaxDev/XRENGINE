using System.Globalization;

namespace XREngine.Rendering;

/// <summary>Immutable sampling policy for resource samplers and browser materials.</summary>
public sealed record BrowserSamplerDescription
{
    public BrowserSamplerDescription(string AddressU = "clamp-to-edge", string AddressV = "clamp-to-edge",
        string MinFilter = "linear", string MagFilter = "linear", string MipmapFilter = "linear",
        string Label = "", float LodMaxClamp = 32, int MaxAnisotropy = 1, float LodMinClamp = 0,
        string? Compare = null, string AddressW = "clamp-to-edge")
    {
        if (!ValidAddress(AddressU) || !ValidAddress(AddressV) || !ValidAddress(AddressW) ||
            !ValidFilter(MinFilter) || !ValidFilter(MagFilter) || !ValidFilter(MipmapFilter) ||
            !float.IsFinite(LodMinClamp) || !float.IsFinite(LodMaxClamp) ||
            LodMinClamp < 0 || LodMinClamp > LodMaxClamp || LodMaxClamp > 32 ||
            Compare is not (null or "less-equal") ||
            MaxAnisotropy is < 1 or > 16 ||
            (MaxAnisotropy > 1 && (MinFilter != "linear" || MagFilter != "linear" || MipmapFilter != "linear")))
            throw new ArgumentException("Browser sampler address, filtering, LOD or anisotropy policy is unsupported.");
        this.AddressU = AddressU;
        this.AddressV = AddressV;
        this.AddressW = AddressW;
        this.MinFilter = MinFilter;
        this.MagFilter = MagFilter;
        this.MipmapFilter = MipmapFilter;
        this.Label = Label;
        this.LodMinClamp = LodMinClamp;
        this.LodMaxClamp = LodMaxClamp;
        this.MaxAnisotropy = MaxAnisotropy;
        this.Compare = Compare;
    }

    public string AddressU { get; }
    public string AddressV { get; }
    public string AddressW { get; }
    public string MinFilter { get; }
    public string MagFilter { get; }
    public string MipmapFilter { get; }
    public string Label { get; }
    /// <summary>Minimum sampled mip level; zero preserves the legacy material sampling contract.</summary>
    public float LodMinClamp { get; }
    public float LodMaxClamp { get; }
    public int MaxAnisotropy { get; }
    /// <summary>Explicit comparison operation for a depth texture, or null for ordinary sampling.</summary>
    public string? Compare { get; }

    internal string ToPipelineJson()
    {
        if (LodMinClamp != 0 || Compare is not null)
            throw new NotSupportedException("The legacy browser material pipeline requires a zero minimum sampler LOD and ordinary sampling.");
        return $"{{\"addressModeU\":\"{AddressU}\",\"addressModeV\":\"{AddressV}\",\"minFilter\":\"{MinFilter}\",\"magFilter\":\"{MagFilter}\",\"mipmapFilter\":\"{MipmapFilter}\",\"lodMaxClamp\":{LodMaxClamp.ToString("R", CultureInfo.InvariantCulture)},\"maxAnisotropy\":{MaxAnisotropy}}}";
    }

    private static bool ValidAddress(string value) => value is "clamp-to-edge" or "repeat" or "mirror-repeat";
    private static bool ValidFilter(string value) => value is "nearest" or "linear";
}
