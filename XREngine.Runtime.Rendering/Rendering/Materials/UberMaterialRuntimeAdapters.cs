using System.Numerics;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Materials;

/// <summary>
/// Explicit runtime bridge for integrations that are not material assets:
/// AudioLink, LTCGI/light volumes, blacklight, and mirror/camera context.
/// </summary>
public static class UberMaterialRuntimeAdapters
{
    [ThreadStatic]
    private static MaterialViewFlags _viewFlags;

    [ThreadStatic]
    private static Vector4 _viewTint;

    public static IAudioLinkProvider? AudioLink { get; set; }

    public static IMaterialEnvironmentProvider? Environment { get; set; }

    public static MaterialViewFlags CurrentViewFlags => _viewFlags;

    public static Vector4 CurrentViewTint => _viewTint;

    public static MaterialViewContextScope PushViewContext(MaterialViewFlags flags, Vector4 tint)
    {
        MaterialViewContextScope scope = new(_viewFlags, _viewTint);
        _viewFlags = flags;
        _viewTint = tint;
        return scope;
    }

    internal static void RestoreViewContext(MaterialViewFlags flags, Vector4 tint)
    {
        _viewFlags = flags;
        _viewTint = tint;
    }

    /// <summary>
    /// Installs one allocation-free draw hook and binds provider-owned stable
    /// resources. Missing providers remain explicit and return false.
    /// </summary>
    public static bool ConfigureMaterial(
        XRMaterial material,
        bool useAudioLink,
        bool useEnvironment,
        bool useViewContext)
    {
        ArgumentNullException.ThrowIfNull(material);

        bool audioAvailable = !useAudioLink || AudioLink is not null;
        bool environmentAvailable = !useEnvironment || Environment is not null;
        if (useAudioLink && AudioLink is not null)
            BindStableTexture(material, AudioLink.DataTexture, "_AudioLinkTexture");

        if (useAudioLink || useEnvironment || useViewContext)
        {
            UberMaterialAdapterBinding binding = new(useAudioLink, useEnvironment, useViewContext);
            material.SettingUniforms += binding.Apply;
        }

        return audioAvailable && environmentAvailable;
    }

    private static void BindStableTexture(XRMaterial material, XRTexture2D texture, string samplerName)
    {
        texture.SamplerName = samplerName;
        List<XRTexture?> textures = new(material.Textures.Count + 1);
        for (int i = 0; i < material.Textures.Count; ++i)
        {
            XRTexture? existing = material.Textures[i];
            if (!string.Equals(existing?.SamplerName, samplerName, StringComparison.Ordinal))
                textures.Add(existing);
        }
        textures.Add(texture);
        material.Textures = [.. textures];
    }

    private sealed class UberMaterialAdapterBinding(
        bool useAudioLink,
        bool useEnvironment,
        bool useViewContext)
    {
        public void Apply(XRMaterialBase _, XRRenderProgram program)
        {
            if (useAudioLink &&
                AudioLink?.TryGetFrame(out AudioLinkFrame audioFrame) == true)
            {
                program.Uniform("_AudioLinkTextureSize", audioFrame.TextureSize);
                program.Uniform("_AudioLinkTime", audioFrame.Time);
                program.Uniform("_AudioLinkHistory", audioFrame.History);
            }

            if (useEnvironment &&
                Environment?.TryGetFrame(out MaterialEnvironmentFrame environmentFrame) == true)
            {
                program.Uniform("_EnvironmentDiffuse", environmentFrame.Diffuse);
                program.Uniform("_EnvironmentSpecular", environmentFrame.Specular);
                program.Uniform("_EnvironmentBlacklight", environmentFrame.Blacklight);
                program.Uniform("_EnvironmentFlags", environmentFrame.Flags);
            }

            if (!useViewContext)
                return;

            program.Uniform("_ViewFlags", (int)_viewFlags);
            program.Uniform("_ViewTint", _viewTint);
        }
    }
}
