using System.Numerics;
using XREngine.Data.Rendering;
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

    /// <summary>Gets one ready linear AudioLink texture for a material binding.</summary>
    public static bool TryGetReadyAudioLinkTexture(out XRTexture2D? texture)
    {
        texture = null;
        IAudioLinkProvider? provider = AudioLink;
        if (provider is null || !provider.TryGetFrame(out AudioLinkFrame frame))
            return false;

        XRTexture2D candidate = provider.DataTexture;
        if (candidate.ImportedColorSpace != ETextureColorSpace.Linear ||
            candidate.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8 ||
            candidate.Mipmaps.Length == 0 ||
            candidate.Mipmaps[0].PixelFormat is not
                (EPixelFormat.Red or EPixelFormat.Rg or EPixelFormat.Rgb or EPixelFormat.Rgba) ||
            candidate.Mipmaps[0].PixelType is not
                (EPixelType.UnsignedByte or EPixelType.HalfFloat or EPixelType.Float) ||
            candidate.Width < 4 || candidate.Height < 1 ||
            !float.IsFinite(frame.TextureSize.X) || !float.IsFinite(frame.TextureSize.Y) ||
            frame.TextureSize.X != candidate.Width || frame.TextureSize.Y != candidate.Height)
            return false;

        texture = candidate;
        return true;
    }

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
        bool useViewContext,
        out bool audioReady)
    {
        ArgumentNullException.ThrowIfNull(material);

        bool hasReadyAudio = TryGetReadyAudioLinkTexture(out XRTexture2D? audioTexture);
        audioReady = useAudioLink && hasReadyAudio;
        bool audioAvailable = !useAudioLink || hasReadyAudio;
        bool environmentAvailable = !useEnvironment || Environment is not null;
        if (useAudioLink && hasReadyAudio)
            BindStableTexture(material, audioTexture!, "_AudioLinkTexture");

        if (useAudioLink || useEnvironment || useViewContext)
            material.BindingPublishers.Add(new UberMaterialAdapterBinding(useAudioLink, useEnvironment, useViewContext));

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

    /// <summary>
    /// Publishes the adapter values through a typed publisher. A uniform callback
    /// would make the backend capture a new binding snapshot for every draw of the
    /// material. The generation advances only when a published value changes, so a
    /// material without AudioLink keeps its snapshot while the view context and the
    /// environment stay the same.
    /// </summary>
    private sealed class UberMaterialAdapterBinding(
        bool useAudioLink,
        bool useEnvironment,
        bool useViewContext) : IRenderBindingPublisher
    {
        private readonly RenderBindingGenerationTracker<AdapterBindingKey> _generation = new();

        public ERenderBindingFrequency Frequency => ERenderBindingFrequency.View;

        public ulong Generation
        {
            get
            {
                bool hasAudio = false;
                AudioLinkFrame audioFrame = default;
                if (useAudioLink)
                    hasAudio = AudioLink?.TryGetFrame(out audioFrame) == true;
                bool hasEnvironment = false;
                MaterialEnvironmentFrame environmentFrame = default;
                if (useEnvironment)
                    hasEnvironment = Environment?.TryGetFrame(out environmentFrame) == true;
                return _generation.Advance(new AdapterBindingKey(
                    hasAudio,
                    audioFrame,
                    hasEnvironment,
                    environmentFrame,
                    useViewContext ? _viewFlags : default,
                    useViewContext ? _viewTint : default));
            }
        }

        public void PublishUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => Apply(materialProgram);

        private void Apply(XRRenderProgram program)
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

    private readonly record struct AdapterBindingKey(
        bool HasAudio,
        AudioLinkFrame Audio,
        bool HasEnvironment,
        MaterialEnvironmentFrame Environment,
        MaterialViewFlags ViewFlags,
        Vector4 ViewTint);
}
