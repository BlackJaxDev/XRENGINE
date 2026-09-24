using System;
using System.Numerics;
using XREngine.Rendering.Info;

namespace XREngine.Rendering.Commands;

public sealed partial class RenderCommandCollection
{
    private const int S13aManifestMaximumRows = 65536;

    /// <summary>
    /// Copies one complete published resident image for diagnostics. The read
    /// scope protects the package and its lease while all spans are consumed;
    /// the returned manifest contains only owned arrays, scalars and strings.
    /// </summary>
    public S13aIdentityManifest CaptureS13aIdentityManifest()
    {
        using var readScope = EnterRenderingBufferReadScope();
        BackendReadyFramePackage package = _renderingBackendReadyPackage;
        if (package.State != EBackendReadyFramePackageState.Published)
            return new S13aIdentityManifest
            {
                IncompleteReason = "The selected package is not published.",
                PackageIdentity = package.Identity,
            };

        if (!package.TryGetCanonicalPublicationSnapshot(out AdvancedGpuScenePublicationSnapshot snapshot))
            return new S13aIdentityManifest
            {
                IncompleteReason = "The selected package has no retained canonical publication snapshot.",
                PackageIdentity = package.Identity,
                PackageGeneration = package.PackageGeneration,
                SourceRevision = package.SourceRevision,
                Publication = package.CanonicalScenePublication,
            };

        ReadOnlySpan<AdvancedDrawSubmissionRecord> rows = snapshot.Submission.Records;
        ReadOnlySpan<AdvancedManagedDeformationSourceRow> sources = snapshot.Submission.DeformationSources;
        ReadOnlySpan<BackendReadyRenderPass> packagePasses = package.Passes;
        if (rows.Length != sources.Length ||
            rows.Length > S13aManifestMaximumRows ||
            package.CommandCount > S13aManifestMaximumRows ||
            packagePasses.Length > S13aManifestMaximumRows ||
            package.CanonicalViews.Length > S13aManifestMaximumRows)
            return new S13aIdentityManifest
            {
                IncompleteReason = rows.Length != sources.Length
                    ? "Submission and source sidecars are not aligned."
                    : "The published package exceeds the bounded manifest capacity.",
                TotalSubmissionCount = rows.Length,
                PackageIdentity = package.Identity,
                PackageGeneration = package.PackageGeneration,
                SourceRevision = package.SourceRevision,
                PackageCommandCount = package.CommandCount,
                PackageMeshCommandCount = package.MeshCommandCount,
                SubmissionResolution = package.SubmissionResolution,
                Publication = package.CanonicalScenePublication,
            };

        S13aIdentityManifestSubmission[] submissions = new S13aIdentityManifestSubmission[rows.Length];
        bool labelsComplete = true;
        for (int index = 0; index < rows.Length; index++)
        {
            AdvancedDrawSubmissionRecord row = rows[index];
            AdvancedManagedDeformationSourceRow source = sources[index];
            var renderer = source.Renderer;
            var sourceAsset = renderer?.SourceSubMeshAsset;
            string? label = source.Source is RenderCommandMesh3D meshCommand
                ? meshCommand.GpuProfilingLabel
                : null;
            RenderInfo3D? liveOwner = (source.Source as RenderCommandMesh3D)?.OwnerRenderInfo as RenderInfo3D;
            GpuSceneOwnerSnapshot renderOwner = source.Source is RenderCommandMesh3D ownerCommand
                ? ownerCommand.CaptureGpuSceneSnapshot().Owner
                : default;
            label ??= sourceAsset?.Name ?? renderer?.Name;
            if (string.IsNullOrWhiteSpace(label))
                labelsComplete = false;

            // Resolve both transform handles from this retained publication,
            // not from the mutable live command. S13b compares these rows when
            // motion stops and the prior matrix must settle on the next swap.
            float[]? currentWorld = null;
            float[]? previousWorld = null;
            uint? publishedLayerMask = null;
            if (snapshot.Draws.TryGet(row.Draw, out AdvancedDrawRecord draw))
            {
                if (snapshot.Instances.TryGet(draw.Instance, out AdvancedInstanceRecord instance))
                    publishedLayerMask = instance.LayerMask;
                if (snapshot.Transforms.TryGet(draw.CurrentTransform, out AdvancedTransformRecord current))
                    currentWorld = CopyMatrix(in current.World);
                if (snapshot.Transforms.TryGet(draw.PreviousTransform, out AdvancedTransformRecord previous))
                    previousWorld = CopyMatrix(in previous.World);
            }

            // Imported entity identity alone cannot distinguish repeated owners
            // or primitives. A stable fixture key needs the source hierarchy and
            // ordinal, which this retained sidecar does not authoritatively own.
            submissions[index] = new S13aIdentityManifestSubmission(
                index, row.StableQueryKey, row.PrimitiveIndex,
                row.LegacyCommandIndex, row.PassIndex, row.SourceOrder,
                row.Draw, row.Geometry, row.Material, row.Deformation,
                row.InstanceCount, row.Flags, row.StateClass,
                row.CompatibilityReason, row.TemporalEventReason,
                row.DependencySignature, label, sourceAsset?.Name,
                sourceAsset?.ImportedEntityIdentity,
                sourceAsset?.ImportedEntityIdentityIsStable == true,
                FixtureKey: null, FixtureKeyIsStable: false,
                CurrentWorld: currentWorld, PreviousWorld: previousWorld,
                LiveOwnerCastsShadows: liveOwner?.CastsShadows,
                RenderOwnerCastsShadows: renderOwner.Is3D ? renderOwner.CastsShadows : null,
                RenderOwnerLayerMask: renderOwner.Is3D ? renderOwner.LayerMask : null,
                PublishedLayerMask: publishedLayerMask);
        }

        ReadOnlySpan<BackendReadyCanonicalViewRecord> packageViews = package.CanonicalViews;
        S13aIdentityManifestView[] views = new S13aIdentityManifestView[packageViews.Length];
        for (int index = 0; index < packageViews.Length; index++)
        {
            BackendReadyCanonicalViewRecord view = packageViews[index];
            views[index] = new S13aIdentityManifestView(
                view.ViewId, view.ViewGeneration, view.HistoryKey,
                view.SourceCameraIdentity, view.ViewportWidth, view.ViewportHeight,
                view.OutputLayer, view.Flags);
        }

        ReadOnlySpan<BackendReadyCanonicalPassRecord> canonicalPasses = package.CanonicalPasses;
        S13aIdentityManifestPass[] passes = new S13aIdentityManifestPass[packagePasses.Length];
        for (int index = 0; index < packagePasses.Length; index++)
        {
            BackendReadyRenderPass pass = packagePasses[index];
            BackendReadyCanonicalPassRecord canonical = default;
            for (int canonicalIndex = 0; canonicalIndex < canonicalPasses.Length; canonicalIndex++)
            {
                if (canonicalPasses[canonicalIndex].PassIndex != pass.PassIndex)
                    continue;
                canonical = canonicalPasses[canonicalIndex];
                break;
            }

            uint[] members = new uint[pass.CommandCount];
            int memberIndex = 0;
            foreach (RenderCommand command in pass.Commands)
                members[memberIndex++] = command.StableQueryKey;
            passes[index] = new S13aIdentityManifestPass(
                pass.PassIndex, canonical.PassGeneration,
                canonical.DependencySignature, canonical.MembershipSignature,
                pass.CommandCount, pass.MeshCommandCount,
                pass.CommandSetSignature, pass.DependencySignature, members);
        }

        return new S13aIdentityManifest
        {
            Complete = true,
            SourceLabelsComplete = labelsComplete,
            TotalSubmissionCount = rows.Length,
            PackageIdentity = package.Identity,
            PackageGeneration = package.PackageGeneration,
            SourceRevision = package.SourceRevision,
            PackageCommandCount = package.CommandCount,
            PackageMeshCommandCount = package.MeshCommandCount,
            SubmissionResolution = package.SubmissionResolution,
            Publication = package.CanonicalScenePublication,
            Submissions = submissions,
            Views = views,
            Passes = passes,
        };
    }

    private static float[] CopyMatrix(in Matrix4x4 value)
        => [value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44];
}
