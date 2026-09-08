using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Rendering;

/// <summary>Resolves readback words only against the immutable publication that produced them.</summary>
public static class AdvancedPickingResolver
{
    public static bool TryResolve(
        in AdvancedVisibilityEncodedSurface encoded,
        AdvancedGpuScenePublicationSnapshot publication,
        ulong requestGeneration,
        out AdvancedPickingResult result)
    {
        ArgumentNullException.ThrowIfNull(publication);
        result = AdvancedPickingResult.Miss(
            requestGeneration,
            publication.DatabaseEpoch,
            publication.Draws.Sequence,
            encoded.IsValid ? encoded.Metadata.Decode().ViewIndex : 0u);
        if (!encoded.IsValid || publication.Draws.Sequence == 0u)
            return false;

        AdvancedVisibilityLogicalSurface surface = encoded.DecodeLogical();
        AdvancedVisibilityDecodedMetadata metadata = encoded.Metadata.Decode();
        if (metadata.PayloadVersion != AdvancedVisibilityBufferContract.PayloadVersion)
            return false;
        if (!TryResolveLogicalHandle(
                publication.Draws,
                surface.DrawTableIndex,
                out AdvancedGpuHandle drawHandle) ||
            !publication.Draws.TryGet(drawHandle, out AdvancedDrawRecord draw) ||
            !publication.Instances.TryGet(draw.Instance, out _) ||
            !publication.Geometry.TryGet(draw.Geometry, out _) ||
            !publication.Materials.TryGet(draw.Material, out _) ||
            !publication.Transforms.TryGet(draw.CurrentTransform, out _) ||
            !publication.Transforms.TryGet(draw.PreviousTransform, out _) ||
            !publication.EditorIdentities.TryGet(
                draw.EditorIdentity,
                out AdvancedEditorIdentityRecord editorIdentity))
        {
            return false;
        }

        uint selectionId = surface.SelectionId == AdvancedVisibilityBufferContract.InvalidWord
            ? editorIdentity.SelectionId
            : surface.SelectionId;
        if (surface.SelectionId != AdvancedVisibilityBufferContract.InvalidWord &&
            editorIdentity.SelectionId != 0u &&
            editorIdentity.SelectionId != surface.SelectionId)
        {
            return false;
        }

        AdvancedVisibilityDecodedPrimitive primitive = surface.Primitive;
        if (!primitive.IsValid)
            return false;
        RenderInfo? authoringRenderInfo = ResolveAuthoringRenderInfo(
            publication.Submission,
            drawHandle);

        result = new AdvancedPickingResult(
            true,
            requestGeneration,
            publication.DatabaseEpoch,
            publication.Draws.Sequence,
            drawHandle,
            draw.Instance,
            draw.Geometry,
            draw.Material,
            draw.CurrentTransform,
            draw.PreviousTransform,
            draw.EditorIdentity,
            editorIdentity.StableInstanceId,
            editorIdentity.IdentityHigh,
            selectionId,
            draw.PrimitiveSection,
            surface.Producer,
            primitive.IsMeshletOrCluster
                ? AdvancedVisibilityBufferContract.InvalidWord
                : primitive.PrimitiveIndex,
            primitive.IsMeshletOrCluster
                ? primitive.MeshletOrClusterIndex
                : AdvancedVisibilityBufferContract.InvalidWord,
            primitive.IsMeshletOrCluster
                ? primitive.LocalPrimitiveIndex
                : AdvancedVisibilityBufferContract.InvalidWord,
            surface.ViewIndex,
            authoringRenderInfo);
        return true;
    }

    private static RenderInfo? ResolveAuthoringRenderInfo(
        AdvancedSceneSubmissionPublicationSnapshot submission,
        in AdvancedGpuHandle draw)
    {
        if (submission.Sequence == 0u)
            return null;

        ReadOnlySpan<AdvancedDrawSubmissionRecord> records = submission.Records;
        ReadOnlySpan<AdvancedManagedDeformationSourceRow> sources =
            submission.DeformationSources;
        int count = Math.Min(records.Length, sources.Length);
        for (int index = 0; index < count; index++)
        {
            if (records[index].Draw != draw)
                continue;
            return sources[index].AuthoringRenderInfo;
        }

        return null;
    }

    private static bool TryResolveLogicalHandle<T>(
        AdvancedGpuRecordTablePublicationSnapshot<T> table,
        uint logicalIndex,
        out AdvancedGpuHandle handle)
        where T : unmanaged
    {
        handle = AdvancedGpuHandle.Invalid;
        ReadOnlySpan<AdvancedGpuHandleLookup> lookups = table.HandleLookups;
        if (logicalIndex == 0u || logicalIndex >= (uint)lookups.Length)
            return false;

        AdvancedGpuHandleLookup lookup = lookups[checked((int)logicalIndex)];
        if (!lookup.IsResident)
            return false;

        handle = new AdvancedGpuHandle(logicalIndex, lookup.Generation);
        return table.TryGetDenseIndex(handle, out _);
    }
}
