using XREngine.Rendering.Commands;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuAdvancedShadingOutput
{
    private static ulong AuthoredDecalCommandSignature(in AdvancedVisibilityStageBackendRequest request)
        => request.EnableAuthoredDecals && request.BackendReadyPackage is { } package
            ? package.AuthoredDecalCommandSignature : 0;

    private void PrepareAuthoredDecals(in AdvancedVisibilityStageBackendRequest request,
        AdvancedGpuScenePublicationSnapshot snapshot, WebGpuAdvancedShadingFrame frame)
    {
        frame.AuthoredDecalCount = 0;
        if (!request.EnableAuthoredDecals || request.BackendReadyPackage is not { } package) return;
        ReadOnlySpan<uint> selectedCommands = package.AuthoredDecalCommandKeys;
        if (frame.AuthoredDecalIndices.Length < selectedCommands.Length)
            Array.Resize(ref frame.AuthoredDecalIndices, selectedCommands.Length);
        ReadOnlySpan<AdvancedDecalRecord> rows = snapshot.GlobalResources.Decals.PhysicalRecords;
        for (int commandIndex = 0; commandIndex < selectedCommands.Length; commandIndex++)
        {
            int selected = -1;
            for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                ref readonly AdvancedDecalRecord row = ref rows[rowIndex];
                if (row.AuthoredCommandKey == selectedCommands[commandIndex] &&
                    (row.Flags & AdvancedDecalRecord.EnabledFlag) != 0 &&
                    snapshot.GlobalResources.Decals.TryGetDenseIndex(row.Identity, out uint dense) && dense == rowIndex)
                { selected = rowIndex; break; }
            }
            if (selected < 0)
                throw Invalid("AuthoredDecalCaptureMissing", "a selected DeferredDecals command has no matching frozen authored decal producer");
            ref readonly AdvancedDecalRecord decal = ref rows[selected];
            if ((decal.Flags & AdvancedDecalRecord.ForwardOitFlag) != 0)
                throw Invalid("AuthoredDecalOitUnsupported", "forward weighted OIT decals require an exact late-pass lowering");
            if ((decal.Flags & AdvancedDecalRecord.UnsupportedDrawCommandFlag) != 0)
                throw Invalid("AuthoredDecalDrawCommandUnsupported", "the selected decal changes its owned default box draw through a material/raster override, replacement or mutated geometry, transform, instancing, binding publisher, or callback; that draw requires an exact native lowering");
            if ((decal.Flags & AdvancedDecalRecord.UnsupportedAuthoredFlag) != 0)
                throw Invalid("AuthoredDecalContractUnsupported", "the selected decal requires the exact source-free default albedo carrier, an invertible finite affine box, and unmodified collection callbacks; custom surface and callback effects require their own lowering");
            if (!decal.MaskTexture.Handle.IsValid)
                throw Invalid("AuthoredDecalImageMissing", "the selected authored decal lacks its frozen projected image");
            AddGlobal(snapshot, decal.MaskTexture);
            frame.AuthoredDecalIndices[frame.AuthoredDecalCount++] = (uint)selected;
        }
    }
}
