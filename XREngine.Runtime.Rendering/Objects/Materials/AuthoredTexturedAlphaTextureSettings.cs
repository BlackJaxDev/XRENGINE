namespace XREngine.Rendering;

/// <summary>New-family authored metadata omitted by the unchanged raw texture YAML payload.</summary>
public sealed record AuthoredTexturedAlphaTextureSettings(
    Guid BaseColorId,
    PublishedStandardLitTextureSettings BaseColor,
    Guid OpacityId,
    PublishedStandardLitTextureSettings Opacity)
{
    public AuthoredTexturedAlphaTextureSettings() : this(Guid.Empty, default, Guid.Empty, default) { }
}
