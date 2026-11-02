using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>Edits both aliases of a standard lit texture role.</summary>
public static class StandardLitTextureBindingEditor
{
    /// <summary>Replaces one role after the complete source contract is valid.</summary>
    public static bool TryReplace(
        XRMaterial material,
        bool surfaceSelector,
        int selectedIndex,
        XRTexture? replacement,
        out StandardLitTextureBindingEdit edit,
        out string? reason)
    {
        edit = default;
        reason = "The standard lit texture source contract is not valid.";
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitTextureV1 ||
            !StandardLitTextureSurfaceBinding.TryRead(material, out StandardLitTextureSurface surface, out _))
            return false;
        if (replacement is not XRTexture2D replacement2D || replacement2D.IsDestroyed)
        {
            reason = "A standard lit texture role requires a live 2D texture; null is not supported.";
            return false;
        }

        int legacyIndex = -1;
        int surfaceIndex = -1;
        MaterialSurfaceTextureBinding[] bindings = material.SurfaceTextureBindings;
        for (int index = 0; index < bindings.Length; ++index)
        {
            MaterialSurfaceTextureBinding? binding = bindings[index];
            if (binding is null)
                continue;
            int candidateLegacy = binding.Semantic switch
            {
                EMaterialTextureSemantic.BaseColor => 0,
                EMaterialTextureSemantic.Normal => 1,
                EMaterialTextureSemantic.Metallic => 2,
                EMaterialTextureSemantic.Roughness => surface.Normal is not null || surface.Metallic is not null ? 3 : 2,
                _ => -1,
            };
            if (candidateLegacy < 0 ||
                (surfaceSelector ? index != selectedIndex : candidateLegacy != selectedIndex))
                continue;
            legacyIndex = candidateLegacy;
            surfaceIndex = index;
            break;
        }
        if ((uint)legacyIndex >= (uint)material.Textures.Count || surfaceIndex < 0)
        {
            reason = "The selected standard lit slot has no paired semantic texture role.";
            return false;
        }
        MaterialSurfaceTextureBinding previousSurface = bindings[surfaceIndex];
        if (material.Textures[legacyIndex] is not XRTexture2D previous ||
            !ReferenceEquals(previous, previousSurface.Texture))
            return false;
        if (!string.Equals(previous.ResolveSamplerName(legacyIndex),
                replacement2D.ResolveSamplerName(legacyIndex), StringComparison.Ordinal))
        {
            reason = "The replacement sampler name does not match the standard lit slot.";
            return false;
        }
        if (previousSurface.WrapU != replacement2D.UWrap ||
            previousSurface.WrapV != replacement2D.VWrap)
        {
            reason = "The replacement wrapping does not match the standard lit surface metadata.";
            return false;
        }
        bool previousHardwareSrgb = previous.SizedInternalFormat is
            ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8;
        bool replacementHardwareSrgb = replacement2D.SizedInternalFormat is
            ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8;
        if (previous.ImportedColorSpace != replacement2D.ImportedColorSpace ||
            previous.ImportedUsage != replacement2D.ImportedUsage ||
            previousHardwareSrgb != replacementHardwareSrgb)
        {
            reason = "The replacement color space or usage does not match the standard lit role.";
            return false;
        }

        MaterialSurfaceTextureBinding replacementSurface = ReferenceEquals(previous, replacement2D)
            ? previousSurface
            : previousSurface with { Texture = replacement2D };
        edit = new StandardLitTextureBindingEdit(legacyIndex, surfaceIndex,
            previous, replacement2D, previousSurface, replacementSurface);
        if (ReferenceEquals(previous, replacement2D))
        {
            reason = null;
            return true;
        }

        if (!Apply(material, in edit, restore: false, out string? applyReason))
        {
            edit = default;
            reason = applyReason;
            return false;
        }
        bool sourceValid;
        try { sourceValid = StandardLitTextureSurfaceBinding.TryRead(material, out _, out _); }
        catch (Exception) { sourceValid = false; }
        if (!sourceValid)
        {
            bool restored = Apply(material, in edit, restore: true, out string? restoreReason);
            edit = default;
            reason = restored
                ? "The replacement does not satisfy the standard lit texture source contract. The prior aliases were restored."
                : $"The replacement broke the standard lit texture source contract. Restore failed: {restoreReason}";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>Checks both aliases before a guarded restore.</summary>
    public static bool Matches(XRMaterial material, in StandardLitTextureBindingEdit edit, bool previous)
    {
        if ((uint)edit.LegacyIndex >= (uint)material.Textures.Count ||
            (uint)edit.SurfaceIndex >= (uint)material.SurfaceTextureBindings.Length)
            return false;
        return ReferenceEquals(material.Textures[edit.LegacyIndex],
                   previous ? edit.PreviousTexture : edit.ReplacementTexture) &&
               ReferenceEquals(material.SurfaceTextureBindings[edit.SurfaceIndex],
                   previous ? edit.PreviousSurface : edit.ReplacementSurface);
    }

    /// <summary>Writes both aliases or restores their prior values after a failed write.</summary>
    public static bool Apply(XRMaterial material, in StandardLitTextureBindingEdit edit,
        bool restore, out string? reason)
    {
        reason = null;
        if (material.IsDestroyed ||
            (uint)edit.LegacyIndex >= (uint)material.Textures.Count ||
            (uint)edit.SurfaceIndex >= (uint)material.SurfaceTextureBindings.Length)
        {
            reason = "The standard lit texture target is no longer valid.";
            return false;
        }
        if (!Matches(material, in edit, previous: !restore))
        {
            reason = "The standard lit texture aliases changed after this edit was recorded.";
            return false;
        }
        XRTexture destination = restore ? edit.PreviousTexture : edit.ReplacementTexture;
        if (destination.IsDestroyed)
        {
            reason = "The retained standard lit texture was destroyed.";
            return false;
        }

        MaterialSurfaceTextureBinding[] priorBindings = material.SurfaceTextureBindings;
        XRTexture? priorTexture = material.Textures[edit.LegacyIndex];
        MaterialSurfaceTextureBinding[] updatedBindings = [.. priorBindings];
        updatedBindings[edit.SurfaceIndex] = restore ? edit.PreviousSurface : edit.ReplacementSurface;
        string failure = "The material did not accept both standard lit texture aliases.";
        try
        {
            material.SurfaceTextureBindings = updatedBindings;
            material.Textures[edit.LegacyIndex] = restore ? edit.PreviousTexture : edit.ReplacementTexture;
            if (Matches(material, in edit, previous: restore))
                return true;
        }
        catch (Exception exception)
        {
            failure = $"The standard lit texture alias write failed ({exception.GetType().Name}).";
        }

        // Restore each alias even if the other setter rejects the restore.
        try { material.Textures[edit.LegacyIndex] = priorTexture; }
        catch (Exception) { }
        try { material.SurfaceTextureBindings = priorBindings; }
        catch (Exception) { }
        bool legacyRestored = (uint)edit.LegacyIndex < (uint)material.Textures.Count &&
            ReferenceEquals(material.Textures[edit.LegacyIndex], priorTexture);
        bool surfaceRestored = ReferenceEquals(material.SurfaceTextureBindings, priorBindings);
        reason = legacyRestored && surfaceRestored
            ? $"{failure} The prior aliases were restored."
            : $"{failure} The prior aliases could not be restored. Inspect the material before another edit.";
        return false;
    }
}
