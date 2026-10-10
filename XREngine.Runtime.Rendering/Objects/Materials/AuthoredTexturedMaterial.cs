using System.ComponentModel;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

/// <summary>Explicit authored forward texture schema with additive per-role YAML image settings.</summary>
public sealed class AuthoredTexturedMaterial : XRMaterial
{
    private AuthoredTexturedTextureSettings? _restoredTextureSettings;
    private bool _hasTextureSettings;
    private bool _restoringTextureSettings;

    public AuthoredTexturedMaterial() { }

    internal AuthoredTexturedMaterial(ShaderVar[] parameters, XRTexture?[] textures, XRShader shader)
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

    /// <summary>Captures live image settings; metadata-first reads wait for all images and semantic roles before restoration.</summary>
    [Browsable(false), YamlMember(Order = 1000)]
    public AuthoredTexturedTextureSettings? TextureSettings
    {
        get => CaptureSettings() ?? _restoredTextureSettings;
        set
        {
            value?.Validate();
            if (value is not null && HasCompleteTextureGraph(value))
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

    private bool HasCompleteTextureGraph(AuthoredTexturedTextureSettings value)
        => Textures.Count == value.TextureCount && SurfaceTextureBindings.Length == value.TextureCount;

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        if (!_hasTextureSettings && !_restoringTextureSettings && _restoredTextureSettings is { } settings && HasCompleteTextureGraph(settings))
        {
            RestoreSettings(settings);
            SetField(ref _hasTextureSettings, true, publishNotifications: false);
        }
    }

    private AuthoredTexturedTextureSettings? CaptureSettings()
    {
        if (Textures.Count is < 2 or > 4 || SurfaceTextureBindings.Length != Textures.Count) return null;
        XRTexture2D? baseColor = null, normal = null, specular = null, opacity = null;
        foreach (MaterialSurfaceTextureBinding? binding in SurfaceTextureBindings)
        {
            if (binding?.Texture is not XRTexture2D image) return null;
            switch (binding.Semantic)
            {
                case EMaterialTextureSemantic.BaseColor when baseColor is null: baseColor = image; break;
                case EMaterialTextureSemantic.Normal when normal is null: normal = image; break;
                case EMaterialTextureSemantic.Specular when specular is null: specular = image; break;
                case EMaterialTextureSemantic.Opacity when opacity is null: opacity = image; break;
                default: return null;
            }
        }
        if (baseColor is null || normal is null && specular is null) return null;
        AuthoredTexturedTextureSettings settings = new(baseColor.ID, PublishedStandardLitTextureSettings.Capture(baseColor),
            normal?.ID ?? Guid.Empty, normal is null ? null : PublishedStandardLitTextureSettings.Capture(normal),
            specular?.ID ?? Guid.Empty, specular is null ? null : PublishedStandardLitTextureSettings.Capture(specular),
            opacity?.ID ?? Guid.Empty, opacity is null ? null : PublishedStandardLitTextureSettings.Capture(opacity));
        settings.Validate();
        return settings;
    }

    private void RestoreSettings(AuthoredTexturedTextureSettings value)
    {
        value.Validate();
        int roles = 0;
        for (int index = 0; index < value.TextureCount; index++)
        {
            var role = value.RoleAt(index);
            if (Textures[index] is not XRTexture2D image || image.GetType() != typeof(XRTexture2D) || image.ID != role.Id)
                throw Mismatch();
        }
        foreach (MaterialSurfaceTextureBinding? binding in SurfaceTextureBindings)
        {
            if (binding?.Texture is not XRTexture2D image || image.GetType() != typeof(XRTexture2D)) throw Mismatch();
            bool found = false;
            for (int index = 0; index < value.TextureCount; index++)
            {
                var role = value.RoleAt(index);
                if (role.Semantic != binding.Semantic) continue;
                if (image.ID != role.Id || (roles & (1 << index)) != 0) throw Mismatch();
                roles |= 1 << index;
                found = true;
                break;
            }
            if (!found) throw Mismatch();
        }
        if (roles != (1 << value.TextureCount) - 1) throw Mismatch();
        SetField(ref _restoringTextureSettings, true, publishNotifications: false);
        try
        {
            lock (AuthoredTextureSettingsRestoreRegistry.Gate)
            {
                // Check every image before mutating any, including shared objects across materials.
                for (int index = 0; index < value.TextureCount; index++)
                {
                    var role = value.RoleAt(index);
                    AuthoredTextureSettingsRestoreRegistry.Check((XRTexture2D)Textures[index]!, role.Settings);
                    foreach (MaterialSurfaceTextureBinding binding in SurfaceTextureBindings)
                        if (binding.Semantic == role.Semantic) AuthoredTextureSettingsRestoreRegistry.Check((XRTexture2D)binding.Texture, role.Settings);
                }
                for (int index = 0; index < value.TextureCount; index++)
                {
                    var role = value.RoleAt(index);
                    AuthoredTextureSettingsRestoreRegistry.Restore((XRTexture2D)Textures[index]!, role.Settings);
                    foreach (MaterialSurfaceTextureBinding binding in SurfaceTextureBindings)
                        if (binding.Semantic == role.Semantic) AuthoredTextureSettingsRestoreRegistry.Restore((XRTexture2D)binding.Texture, role.Settings);
                }
            }
        }
        finally { SetField(ref _restoringTextureSettings, false, publishNotifications: false); }
    }

    private static InvalidDataException Mismatch()
        => new("AuthoredTextured.TextureSettingsMismatch: settings require exact image identities and every declared semantic role exactly once.");
}
