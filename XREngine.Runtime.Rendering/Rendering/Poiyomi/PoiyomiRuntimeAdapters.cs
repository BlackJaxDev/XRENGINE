using System.Numerics;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Poiyomi;

/// <summary>
/// Explicit runtime bridge for integrations that are not material assets:
/// AudioLink, LTCGI/light volumes, blacklight, and mirror/camera context.
/// </summary>
public static class PoiyomiRuntimeAdapters
{
    [ThreadStatic]
    private static PoiyomiViewFlags _viewFlags;

    [ThreadStatic]
    private static Vector4 _viewTint;

    public static IPoiyomiAudioLinkProvider? AudioLink { get; set; }

    public static IPoiyomiEnvironmentProvider? Environment { get; set; }

    public static PoiyomiViewFlags CurrentViewFlags => _viewFlags;

    public static Vector4 CurrentViewTint => _viewTint;

    public static PoiyomiViewContextScope PushViewContext(PoiyomiViewFlags flags, Vector4 tint)
    {
        PoiyomiViewContextScope scope = new(_viewFlags, _viewTint);
        _viewFlags = flags;
        _viewTint = tint;
        return scope;
    }

    internal static void RestoreViewContext(PoiyomiViewFlags flags, Vector4 tint)
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
            PoiyomiMaterialAdapterBinding binding = new(useAudioLink, useEnvironment, useViewContext);
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

    private sealed class PoiyomiMaterialAdapterBinding(
        bool useAudioLink,
        bool useEnvironment,
        bool useViewContext)
    {
        public void Apply(XRMaterialBase _, XRRenderProgram program)
        {
            if (useAudioLink &&
                AudioLink?.TryGetFrame(out PoiyomiAudioLinkFrame audioFrame) == true)
            {
                program.Uniform("_AudioLinkTextureSize", audioFrame.TextureSize);
                program.Uniform("_AudioLinkTime", audioFrame.Time);
                program.Uniform("_AudioLinkHistory", audioFrame.History);
            }

            if (useEnvironment &&
                Environment?.TryGetFrame(out PoiyomiEnvironmentFrame environmentFrame) == true)
            {
                program.Uniform("_PoiEnvironmentLight", environmentFrame.Diffuse);
                program.Uniform("_PoiEnvironmentSpecular", environmentFrame.Specular);
                program.Uniform("_PoiBlacklight", environmentFrame.Blacklight);
                program.Uniform("_PoiEnvironmentFlags", environmentFrame.Flags);
            }

            if (!useViewContext)
                return;

            program.Uniform("_PoiViewFlags", (int)_viewFlags);
            program.Uniform("_PoiViewTint", _viewTint);
        }
    }
}
