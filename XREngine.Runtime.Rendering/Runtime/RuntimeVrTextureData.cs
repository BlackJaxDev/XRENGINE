namespace XREngine.Rendering;

/// <summary>Owned RGBA pixels copied from a runtime render-model texture.</summary>
public sealed record RuntimeVrTextureData(uint Width, uint Height, byte[] RgbaPixels);
