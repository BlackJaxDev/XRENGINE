using System.ComponentModel;

namespace XREngine.Rendering;

/// <summary>Reflection-cooked outline metadata accompanying the complete ordinary source material.</summary>
public sealed record UberOutlineMaterialProfile
{
    public const uint AlphaMasks = 1;
    public const uint Dissolve = 2;
    public const uint SupportedFeatures = AlphaMasks | Dissolve;
    public const int MaximumSourceTextures = 32;

    public int Version { get; init; } = 1;
    public uint Features { get; init; }
    [DefaultValue(true)]
    public bool RenderTimeEnabled { get; init; } = true;
    public UberOutlineTextureBinding[] TextureBindings { get; init; } = [];
    /// <summary>Complete non-null source image table, preserving original slots including unused base-pass images.</summary>
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
        foreach (UberOutlineTextureBinding binding in complete)
            binding.Settings.ApplyTo(binding.Texture);
    }

    /// <summary>Checks exact sampler-to-source identity without loading assets or substituting images.</summary>
    public bool TryValidateTextureTable(XRMaterial source, out string? reason)
    {
        reason = "UberOutline.ProfileInvalid: version, features, or the bounded source texture table is invalid.";
        if (Version != 1 || (Features & ~SupportedFeatures) != 0 || TextureBindings is null ||
            source.Textures.Count > MaximumSourceTextures ||
            TextureBindings.Length != 3 + ((Features & AlphaMasks) != 0 ? 1 : 0) + ((Features & Dissolve) != 0 ? 5 : 0))
            return false;
        for (int slot = 0; slot < source.Textures.Count; slot++)
            if (source.Textures[slot] is XRTexture texture && texture.GetType() != typeof(XRTexture2D))
                return false;

        for (int index = 0; index < TextureBindings.Length; index++)
        {
            UberOutlineTextureBinding? binding = TextureBindings[index];
            string requiredName = RequiredSamplerName(Features, index);
            reason = "UberOutline.TextureBindingInvalid: every required sampler must resolve unambiguously to its exact original source image and slot.";
            if (binding is null || binding.SamplerName != requiredName || binding.Texture is null ||
                binding.Texture.GetType() != typeof(XRTexture2D) ||
                (uint)binding.SourceTextureSlot >= (uint)source.Textures.Count ||
                !ReferenceEquals(source.Textures[binding.SourceTextureSlot], binding.Texture) ||
                binding.Texture.SamplerName != requiredName)
                return false;

            for (int slot = 0; slot < source.Textures.Count; slot++)
                if (slot != binding.SourceTextureSlot && source.Textures[slot]?.SamplerName == requiredName)
                    return false;

            reason = "UberOutline.TextureSettingsInvalid: preserved texture settings contain unsupported sampler or import values.";
            if (!ValidSettings(binding.Settings))
                return false;
        }
        if (SourceTextureBindings is not null && !TryValidateCompleteSourceTable(source, SourceTextureBindings, out reason))
            return false;
        if (SourceTextureBindings is not null)
            foreach (UberOutlineTextureBinding active in TextureBindings)
                foreach (UberOutlineTextureBinding complete in SourceTextureBindings)
                    if (complete.SourceTextureSlot == active.SourceTextureSlot && complete.Settings != active.Settings)
                    {
                        reason = "UberOutline.TextureSettingsAliasMismatch: active and complete source metadata must preserve identical settings for the same original image.";
                        return false;
                    }
        reason = null;
        return true;
    }

    private static bool TryValidateCompleteSourceTable(XRMaterial source, UberOutlineTextureBinding[] complete, out string? reason)
    {
        reason = "UberOutline.SourceTextureTableInvalid: complete source metadata must preserve every real 2D image, original slot, resolved name, and consistent alias settings.";
        if (complete.Length > MaximumSourceTextures || source.Textures.Count > MaximumSourceTextures)
            return false;
        int images = 0;
        for (int slot = 0; slot < source.Textures.Count; slot++)
        {
            XRTexture? texture = source.Textures[slot];
            if (texture is null) continue;
            if (texture.GetType() != typeof(XRTexture2D)) return false;
            images++;
            int matches = 0;
            for (int index = 0; index < complete.Length; index++)
            {
                UberOutlineTextureBinding? binding = complete[index];
                if (binding is null) return false;
                if (binding.SourceTextureSlot != slot) continue;
                if (!ReferenceEquals(binding.Texture, texture) ||
                    binding.SamplerName != texture.ResolveSamplerName(slot) || !ValidSettings(binding.Settings))
                    return false;
                matches++;
            }
            if (matches != 1) return false;
        }
        if (images != complete.Length) return false;
        for (int index = 0; index < complete.Length; index++)
            for (int other = index + 1; other < complete.Length; other++)
                if (ReferenceEquals(complete[index].Texture, complete[other].Texture) && complete[index].Settings != complete[other].Settings)
                    return false;
        reason = null;
        return true;
    }

    private static bool ValidSettings(PublishedStandardLitTextureSettings settings)
        => float.IsFinite(settings.MaxAnisotropy) && settings.MaxAnisotropy is >= 1 and <= 16 &&
            settings.MaxAnisotropy == MathF.Truncate(settings.MaxAnisotropy) && Enum.IsDefined(settings.CompareFunc) &&
            Enum.IsDefined(settings.ImportedColorSpace) && Enum.IsDefined(settings.ImportedUsage);

    /// <summary>Returns canonical active samplers in production binding order.</summary>
    public static string RequiredSamplerName(uint features, int index)
    {
        if ((features & ~SupportedFeatures) != 0)
            throw new ArgumentOutOfRangeException(nameof(features));
        if (index < 3)
            return index switch { 0 => "_MainTex", 1 => "_OutlineTexture", 2 => "_OutlineMask", _ => throw new ArgumentOutOfRangeException(nameof(index)) };
        if ((features & AlphaMasks) != 0)
        {
            if (index == 3) return "_AlphaMask";
            index--;
        }
        if ((features & Dissolve) == 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return index switch
        {
            3 => "_DissolveNoiseTexture",
            4 => "_DissolveDetailNoise",
            5 => "_DissolveMask",
            6 => "_DissolveEdgeGradient",
            7 => "_DissolveEdgeTexture",
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }
}
