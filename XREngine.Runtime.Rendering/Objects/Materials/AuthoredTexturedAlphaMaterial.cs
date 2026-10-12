using System.ComponentModel;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

/// <summary>
/// Explicit authored two-image schema. Only this new material type adds YAML
/// metadata; ordinary XRMaterial and raw texture serialization stay unchanged.
/// </summary>
public sealed class AuthoredTexturedAlphaMaterial : XRMaterial
{
    private AuthoredTexturedAlphaTextureSettings? _restoredTextureSettings;
    private bool _hasTextureSettings;
    private bool _restoringTextureSettings;

    public AuthoredTexturedAlphaMaterial() { }

    internal AuthoredTexturedAlphaMaterial(ShaderVar[] parameters, XRTexture?[] textures, XRShader shader)
        : base(parameters, textures, shader)
    {
        lock (AuthoredTextureSettingsRestoreRegistry.Gate)
            foreach (XRTexture? texture in textures)
                if (texture is XRTexture2D image)
                    AuthoredTextureSettingsRestoreRegistry.Remember(image, PublishedStandardLitTextureSettings.Capture(image));
        SetField(ref _hasTextureSettings, true, publishNotifications: false);
    }

    [YamlIgnore]
    internal bool HasAuthoredTextureSettings => _hasTextureSettings;

    /// <summary>
    /// Captures current image-owned settings. Hydration retains early metadata
    /// until both texture lists arrive, then validates the complete graph before
    /// restoring any image; missing or mismatched identities cannot be admitted.
    /// </summary>
    [Browsable(false), YamlMember(Order = 1000)]
    public AuthoredTexturedAlphaTextureSettings? TextureSettings
    {
        get => Textures.Count == 2 && Textures[0] is XRTexture2D baseColor && Textures[1] is XRTexture2D opacity
            ? new(baseColor.ID, PublishedStandardLitTextureSettings.Capture(baseColor),
                opacity.ID, PublishedStandardLitTextureSettings.Capture(opacity))
            : _restoredTextureSettings;
        set
        {
            if (value is not null && HasCompleteTextureGraph)
            {
                RestoreSettings(value);
                SetField(ref _restoredTextureSettings, value, publishNotifications: false);
                SetField(ref _hasTextureSettings, true, publishNotifications: false);
                return;
            }
            SetField(ref _restoredTextureSettings, value, publishNotifications: false);
            SetField(ref _hasTextureSettings, false, publishNotifications: false);
        }
    }

    [YamlIgnore]
    private bool HasCompleteTextureGraph => Textures.Count == 2 && SurfaceTextureBindings.Length == 2;

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        if (!_hasTextureSettings && !_restoringTextureSettings && _restoredTextureSettings is { } settings && HasCompleteTextureGraph)
        {
            RestoreSettings(settings);
            SetField(ref _hasTextureSettings, true, publishNotifications: false);
        }
    }

    private void RestoreSettings(AuthoredTexturedAlphaTextureSettings value)
    {
        if (Textures[0] is not XRTexture2D baseColor || Textures[1] is not XRTexture2D opacity ||
            value.BaseColorId == Guid.Empty || value.OpacityId == Guid.Empty ||
            baseColor.ID != value.BaseColorId || opacity.ID != value.OpacityId ||
            value.BaseColorId == value.OpacityId && value.BaseColor != value.Opacity)
            throw new InvalidDataException("AuthoredTexturedAlpha.TextureSettingsMismatch: settings require exact image identities and consistent shared-image metadata.");
        int roles = 0;
        foreach (MaterialSurfaceTextureBinding binding in SurfaceTextureBindings)
        {
            if (binding.Texture is not XRTexture2D image ||
                !(binding.Semantic == EMaterialTextureSemantic.BaseColor && image.ID == value.BaseColorId ||
                binding.Semantic == EMaterialTextureSemantic.Opacity && image.ID == value.OpacityId))
                throw new InvalidDataException("AuthoredTexturedAlpha.TextureSettingsMismatch: texture metadata does not match the authored semantic roles.");
            int bit = binding.Semantic == EMaterialTextureSemantic.BaseColor ? 1 : 2;
            if ((roles & bit) != 0)
                throw new InvalidDataException("AuthoredTexturedAlpha.TextureSettingsMismatch: each authored semantic role must appear exactly once.");
            roles |= bit;
        }
        if (roles != 3)
            throw new InvalidDataException("AuthoredTexturedAlpha.TextureSettingsMismatch: both authored semantic roles are required.");
        SetField(ref _restoringTextureSettings, true, publishNotifications: false);
        try
        {
            lock (AuthoredTextureSettingsRestoreRegistry.Gate)
            {
                AuthoredTextureSettingsRestoreRegistry.Check(baseColor, value.BaseColor, "AuthoredTexturedAlpha.TextureSettingsConflict");
                AuthoredTextureSettingsRestoreRegistry.Check(opacity, value.Opacity, "AuthoredTexturedAlpha.TextureSettingsConflict");
                foreach (MaterialSurfaceTextureBinding binding in SurfaceTextureBindings)
                    AuthoredTextureSettingsRestoreRegistry.Check((XRTexture2D)binding.Texture,
                        binding.Semantic == EMaterialTextureSemantic.BaseColor ? value.BaseColor : value.Opacity, "AuthoredTexturedAlpha.TextureSettingsConflict");
                AuthoredTextureSettingsRestoreRegistry.Restore(baseColor, value.BaseColor);
                AuthoredTextureSettingsRestoreRegistry.Restore(opacity, value.Opacity);
                foreach (MaterialSurfaceTextureBinding binding in SurfaceTextureBindings)
                    AuthoredTextureSettingsRestoreRegistry.Restore((XRTexture2D)binding.Texture, binding.Semantic == EMaterialTextureSemantic.BaseColor ? value.BaseColor : value.Opacity);
            }
        }
        finally { SetField(ref _restoringTextureSettings, false, publishNotifications: false); }
    }

}
