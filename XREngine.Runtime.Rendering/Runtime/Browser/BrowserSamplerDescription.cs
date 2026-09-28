namespace XREngine.Rendering;

/// <summary>Immutable baseline sampling policy. Anisotropy and comparison sampling require a wider profile.</summary>
public sealed record BrowserSamplerDescription(string AddressU = "clamp-to-edge", string AddressV = "clamp-to-edge",
    string MinFilter = "linear", string MagFilter = "linear", string MipmapFilter = "linear", string Label = "");
