using YamlDotNet.Serialization;
using XREngine.Rendering.Materials;

namespace XREngine.Rendering;

/// <summary>Per-role authored settings omitted by the unchanged raw texture YAML payload.</summary>
public sealed record AuthoredTexturedTextureSettings(
    Guid BaseColorId,
    PublishedStandardLitTextureSettings BaseColor,
    Guid NormalId,
    PublishedStandardLitTextureSettings? Normal,
    Guid SpecularId,
    PublishedStandardLitTextureSettings? Specular,
    Guid OpacityId,
    PublishedStandardLitTextureSettings? Opacity)
{
    public AuthoredTexturedTextureSettings() : this(Guid.Empty, default, Guid.Empty, null, Guid.Empty, null, Guid.Empty, null) { }

    [YamlIgnore]
    public int TextureFlags => (Normal.HasValue ? 1 : 0) | (Specular.HasValue ? 2 : 0) | (Opacity.HasValue ? 4 : 0);

    internal int TextureCount => 1 + (Normal.HasValue ? 1 : 0) + (Specular.HasValue ? 1 : 0) + (Opacity.HasValue ? 1 : 0);

    internal (EMaterialTextureSemantic Semantic, Guid Id, PublishedStandardLitTextureSettings Settings) RoleAt(int index)
    {
        if (index == 0) return (EMaterialTextureSemantic.BaseColor, BaseColorId, BaseColor);
        if (Normal is { } normal && --index == 0) return (EMaterialTextureSemantic.Normal, NormalId, normal);
        if (Specular is { } specular && --index == 0) return (EMaterialTextureSemantic.Specular, SpecularId, specular);
        if (Opacity is { } opacity && --index == 0) return (EMaterialTextureSemantic.Opacity, OpacityId, opacity);
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    internal void Validate()
    {
        if (TextureFlags is not (1 or 2 or 3 or 5 or 6 or 7) || BaseColorId == Guid.Empty ||
            (NormalId != Guid.Empty) != Normal.HasValue || (SpecularId != Guid.Empty) != Specular.HasValue ||
            (OpacityId != Guid.Empty) != Opacity.HasValue)
            throw new InvalidDataException("AuthoredTextured.TextureSettingsMismatch: every present role requires exact image identity and settings.");
        for (int index = 0; index < TextureCount; index++)
        {
            PublishedStandardLitTextureSettings settings = RoleAt(index).Settings;
            if (!float.IsFinite(settings.MaxAnisotropy) || settings.MaxAnisotropy is < 1 or > 16 ||
                settings.MaxAnisotropy != MathF.Truncate(settings.MaxAnisotropy) || !Enum.IsDefined(settings.CompareFunc) ||
                !Enum.IsDefined(settings.ImportedColorSpace) || !Enum.IsDefined(settings.ImportedUsage))
                throw new InvalidDataException("AuthoredTextured.TextureSettingsMismatch: image settings require finite integer anisotropy from one through sixteen and defined sampler/import enums.");
        }
        for (int left = 0; left < TextureCount; left++)
            for (int right = left + 1; right < TextureCount; right++)
                if (RoleAt(left).Id == RoleAt(right).Id && RoleAt(left).Settings != RoleAt(right).Settings)
                    throw new InvalidDataException("AuthoredTextured.TextureSettingsConflict: shared image roles require identical settings.");
    }
}
