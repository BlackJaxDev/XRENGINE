using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Rendering;

/// <summary>Resolves a native logical draw index through its retained scene publication.</summary>
public static class EditorPickingNativeResolver
{
    public static bool TryResolve(
        in EditorPickingSnapshotLease source,
        AdvancedGpuScenePublicationSnapshot publication,
        uint logicalDrawIndex,
        ulong requestGeneration,
        uint viewIndex,
        out EditorPickingResult result)
    {
        ArgumentNullException.ThrowIfNull(publication);
        result = EditorPickingResult.Miss(requestGeneration,
            source.PackageIdentity, source.PackageGeneration, viewIndex);
        if (!source.IsValid || logicalDrawIndex is 0u or uint.MaxValue ||
            publication.Draws.Sequence == 0u)
            return false;

        ReadOnlySpan<AdvancedGpuHandleLookup> lookups = publication.Draws.HandleLookups;
        if (logicalDrawIndex >= (uint)lookups.Length)
            return false;

        AdvancedGpuHandleLookup lookup = lookups[checked((int)logicalDrawIndex)];
        if (!lookup.IsResident)
            return false;

        AdvancedGpuHandle drawHandle = new(logicalDrawIndex, lookup.Generation);
        if (!publication.Draws.TryGet(drawHandle, out AdvancedDrawRecord draw) ||
            !publication.EditorIdentities.TryGet(draw.EditorIdentity, out _))
            return false;

        AdvancedSceneSubmissionPublicationSnapshot submission = publication.Submission;
        if (submission.Sequence == 0u)
            return false;

        ReadOnlySpan<AdvancedDrawSubmissionRecord> records = submission.Records;
        ReadOnlySpan<AdvancedManagedDeformationSourceRow> sources =
            submission.DeformationSources;
        int count = Math.Min(records.Length, sources.Length);
        for (int index = 0; index < count; index++)
        {
            if (records[index].Draw != drawHandle)
                continue;

            RenderInfo? renderInfo = sources[index].AuthoringRenderInfo;
            if (renderInfo is null)
                return false;

            result = new EditorPickingResult(
                true,
                requestGeneration,
                source.PackageIdentity,
                source.PackageGeneration,
                viewIndex,
                EditorPickingSourceKind.NativeDraw,
                logicalDrawIndex,
                renderInfo,
                (renderInfo as RenderInfo3D)?.OwnerRenderableMesh);
            return true;
        }

        return false;
    }
}
