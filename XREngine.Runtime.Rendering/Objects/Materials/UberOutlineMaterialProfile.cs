using System.ComponentModel;

namespace XREngine.Rendering;

/// <summary>Reflection-cooked outline metadata accompanying the complete ordinary source material.</summary>
public sealed record UberOutlineMaterialProfile
{
    public const uint AlphaMasks = 1;
    public const uint Dissolve = 2;
    /// <summary>A source texture-array flipbook that controls the final alpha, and so the outline coverage.</summary>
    public const uint FlipbookAlpha = 4;
    public const uint SupportedFeatures = AlphaMasks | Dissolve | FlipbookAlpha;
    public const int MaximumSourceTextures = 32;
    /// <summary>The only active sampler that binds a texture array instead of a plain image.</summary>
    public const string FlipbookArraySamplerName = "_FlipbookTexArray";

    public int Version { get; init; } = 1;
    public uint Features { get; init; }
    [DefaultValue(true)]
    public bool RenderTimeEnabled { get; init; } = true;
    public UberOutlineTextureBinding[] TextureBindings { get; init; } = [];
    /// <summary>
    /// Complete non-null source image table, preserving original slots including unused base-pass images.
    /// A texture array in this table carries its array profile, so the base pass keeps its layer settings.
    /// </summary>
    public UberOutlineTextureBinding[]? SourceTextureBindings { get; init; }

    /// <summary>
    /// Restores omitted settings after reflection decoding has created the material and its images.
    /// The caller must supply the detached decoded material, never a projection borrowing authored images.
    /// Validation completes before any decoded texture is changed; source slots and sampler names stay intact.
    /// </summary>
    public void RestoreDecodedTextures(XRMaterial source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!TryValidateTextureTable(source, out string? reason))
            throw new InvalidDataException(reason);
        UberOutlineTextureBinding[] complete = SourceTextureBindings ?? TextureBindings;
        if (!TryValidateCompleteSourceTable(source, complete, out reason))
            throw new InvalidDataException(reason);
        // The array profiles validate their complete layer graphs before any setting changes.
        foreach (UberOutlineTextureBinding binding in complete)
            if (binding.Texture is XRTexture2DArray array)
                binding.ArrayProfile!.Validate(array);
        foreach (UberOutlineTextureBinding binding in complete)
        {
            if (binding.Texture is XRTexture2DArray array)
                binding.ArrayProfile!.Restore(array);
            else
                binding.Settings.ApplyTo((XRTexture2D)binding.Texture);
        }
    }

    /// <summary>Checks exact sampler-to-source identity without loading assets or substituting images.</summary>
    public bool TryValidateTextureTable(XRMaterial source, out string? reason)
    {
        reason = "UberOutline.ProfileInvalid: version, features, or the bounded source texture table is invalid.";
        if (Version != 1 || (Features & ~SupportedFeatures) != 0 || TextureBindings is null ||
            source.Textures.Count > MaximumSourceTextures ||
            TextureBindings.Length != RequiredTextureCount(Features))
            return false;
        for (int slot = 0; slot < source.Textures.Count; slot++)
            if (source.Textures[slot] is XRTexture texture && !IsSupportedImageType(texture))
                return false;

        for (int index = 0; index < TextureBindings.Length; index++)
        {
            UberOutlineTextureBinding? binding = TextureBindings[index];
            string requiredName = RequiredSamplerName(Features, index);
            reason = "UberOutline.TextureBindingInvalid: every required sampler must resolve unambiguously to its exact original source image and slot.";
            bool array = requiredName == FlipbookArraySamplerName;
            if (binding is null || binding.SamplerName != requiredName || binding.Texture is null ||
                binding.Texture.GetType() != (array ? typeof(XRTexture2DArray) : typeof(XRTexture2D)) ||
                (uint)binding.SourceTextureSlot >= (uint)source.Textures.Count ||
                !ReferenceEquals(source.Textures[binding.SourceTextureSlot], binding.Texture) ||
                binding.Texture.SamplerName != requiredName)
                return false;

            for (int slot = 0; slot < source.Textures.Count; slot++)
                if (slot != binding.SourceTextureSlot && source.Textures[slot]?.SamplerName == requiredName)
                    return false;

            reason = "UberOutline.TextureSettingsInvalid: preserved texture settings contain unsupported sampler or import values.";
            if (!ValidBindingSettings(binding))
                return false;
        }
        if (SourceTextureBindings is not null && !TryValidateCompleteSourceTable(source, SourceTextureBindings, out reason))
            return false;
        if (SourceTextureBindings is not null)
            foreach (UberOutlineTextureBinding active in TextureBindings)
                foreach (UberOutlineTextureBinding complete in SourceTextureBindings)
                    if (complete.SourceTextureSlot == active.SourceTextureSlot &&
                        (complete.Settings != active.Settings || complete.ArrayProfile?.ArrayId != active.ArrayProfile?.ArrayId))
                    {
                        reason = "UberOutline.TextureSettingsAliasMismatch: active and complete source metadata must preserve identical settings for the same original image.";
                        return false;
                    }
        reason = null;
        return true;
    }

    private static bool TryValidateCompleteSourceTable(XRMaterial source, UberOutlineTextureBinding[] complete, out string? reason)
    {
        reason = "UberOutline.SourceTextureTableInvalid: complete source metadata must preserve every real 2D image or texture array, original slot, resolved name, and consistent alias settings.";
        if (complete.Length > MaximumSourceTextures || source.Textures.Count > MaximumSourceTextures)
            return false;
        int images = 0;
        for (int slot = 0; slot < source.Textures.Count; slot++)
        {
            XRTexture? texture = source.Textures[slot];
            if (texture is null) continue;
            if (!IsSupportedImageType(texture)) return false;
            images++;
            int matches = 0;
            for (int index = 0; index < complete.Length; index++)
            {
                UberOutlineTextureBinding? binding = complete[index];
                if (binding is null) return false;
                if (binding.SourceTextureSlot != slot) continue;
                if (!ReferenceEquals(binding.Texture, texture) ||
                    binding.SamplerName != texture.ResolveSamplerName(slot) || !ValidBindingSettings(binding))
                    return false;
                matches++;
            }
            if (matches != 1) return false;
        }
        if (images != complete.Length) return false;
        for (int index = 0; index < complete.Length; index++)
            for (int other = index + 1; other < complete.Length; other++)
                if (ReferenceEquals(complete[index].Texture, complete[other].Texture) &&
                    (complete[index].Settings != complete[other].Settings ||
                     complete[index].ArrayProfile?.ArrayId != complete[other].ArrayProfile?.ArrayId))
                    return false;
        reason = null;
        return true;
    }

    private static bool IsSupportedImageType(XRTexture texture)
        => texture.GetType() == typeof(XRTexture2D) || texture.GetType() == typeof(XRTexture2DArray);

    /// <summary>
    /// Checks the carried settings without allocating, because draw validation calls it.
    /// A plain image needs valid settings and no array profile. A texture array needs a
    /// profile for the same array and layer count, and no plain image settings. The decoder
    /// checks the complete layer graph once, in <see cref="RestoreDecodedTextures"/>.
    /// </summary>
    private static bool ValidBindingSettings(UberOutlineTextureBinding binding)
        => binding.Texture switch
        {
            XRTexture2DArray array => binding.Settings == default && binding.ArrayProfile is { } profile &&
                profile.ArrayId == array.ID && profile.LayerRoles is not null && array.Textures is not null &&
                profile.LayerRoles.Length == array.Textures.Length,
            XRTexture2D => binding.ArrayProfile is null && ValidSettings(binding.Settings),
            _ => false,
        };

    private static bool ValidSettings(PublishedStandardLitTextureSettings settings)
        => float.IsFinite(settings.MaxAnisotropy) && settings.MaxAnisotropy is >= 1 and <= 16 &&
            settings.MaxAnisotropy == MathF.Truncate(settings.MaxAnisotropy) && Enum.IsDefined(settings.CompareFunc) &&
            Enum.IsDefined(settings.ImportedColorSpace) && Enum.IsDefined(settings.ImportedUsage);

    /// <summary>Returns the number of active samplers that the selected feature closure binds.</summary>
    public static int RequiredTextureCount(uint features)
        => 3 + ((features & AlphaMasks) != 0 ? 1 : 0) + ((features & Dissolve) != 0 ? 5 : 0) +
            ((features & FlipbookAlpha) != 0 ? 2 : 0);

    /// <summary>Returns canonical active samplers in production binding order.</summary>
    public static string RequiredSamplerName(uint features, int index)
    {
        if ((features & ~SupportedFeatures) != 0)
            throw new ArgumentOutOfRangeException(nameof(features));
        if (index < 3)
            return index switch { 0 => "_MainTex", 1 => "_OutlineTexture", 2 => "_OutlineMask", _ => throw new ArgumentOutOfRangeException(nameof(index)) };
        index -= 3;
        if ((features & AlphaMasks) != 0)
        {
            if (index == 0) return "_AlphaMask";
            index--;
        }
        if ((features & Dissolve) != 0)
        {
            if (index < 5)
                return index switch
                {
                    0 => "_DissolveNoiseTexture",
                    1 => "_DissolveDetailNoise",
                    2 => "_DissolveMask",
                    3 => "_DissolveEdgeGradient",
                    _ => "_DissolveEdgeTexture",
                };
            index -= 5;
        }
        if ((features & FlipbookAlpha) != 0 && index is 0 or 1)
            return index == 0 ? FlipbookArraySamplerName : "_FlipbookMask";
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    /// <summary>Returns the fixed group 2 texture binding of an active sampler. Its sampler uses the next binding.</summary>
    public static uint RequiredTextureBinding(string samplerName)
        => samplerName switch
        {
            "_MainTex" => 0,
            "_OutlineTexture" => 2,
            "_OutlineMask" => 4,
            "_AlphaMask" => 6,
            "_DissolveNoiseTexture" => 8,
            "_DissolveDetailNoise" => 10,
            "_DissolveMask" => 12,
            "_DissolveEdgeGradient" => 14,
            "_DissolveEdgeTexture" => 16,
            FlipbookArraySamplerName => 18,
            "_FlipbookMask" => 20,
            _ => throw new ArgumentOutOfRangeException(nameof(samplerName)),
        };
}
