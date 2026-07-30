using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// CPU reference for the bounds-checked GPU visibility decoder.
/// </summary>
public static class AdvancedVisibilityDecoder
{
    public static bool TryResolve(
        in AdvancedVisibilityEncodedSurface encoded,
        ReadOnlySpan<AdvancedDrawRecord> draws,
        ReadOnlySpan<AdvancedMaterialRecord> materials,
        out AdvancedVisibilityResolvedSurface resolved)
    {
        resolved = default;
        if (!encoded.IsValid)
            return false;

        AdvancedVisibilityLogicalSurface surface = encoded.DecodeLogical();
        uint drawIndex = surface.DrawTableIndex;
        if (drawIndex == 0u || drawIndex > (uint)draws.Length)
            return false;

        AdvancedDrawRecord draw = draws[checked((int)drawIndex - 1)];
        uint materialIndex = draw.Material.Index;
        if (!draw.Material.IsValid ||
            materialIndex == 0u ||
            materialIndex > (uint)materials.Length)
        {
            return false;
        }

        AdvancedMaterialRecord material =
            materials[checked((int)materialIndex - 1)];
        if (material.StableRowId != materialIndex ||
            material.Generation != draw.Material.Generation)
        {
            return false;
        }

        resolved = new AdvancedVisibilityResolvedSurface(
            surface,
            draw.Instance,
            draw.Geometry,
            draw.Material,
            draw.CurrentTransform,
            draw.PreviousTransform,
            draw.EditorIdentity,
            material.ShadingKernelId,
            draw.PrimitiveSection);
        return true;
    }
}
