using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class AdvancedGpuSceneUnchangedRowGateTests
{
    private const int MaximumRecoveryPublications = 4;

    [Test]
    public void UnchangedSceneSkipsWholeScenePlanAndKeepsRows()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D command);
        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(0);
        AdvancedGpuScenePublicationReference committed = fixture.Publisher.CurrentPublication;
        fixture.Publisher.TryGetCanonicalHandles(0u, out AdvancedGpuHandle draw, out _, out _)
            .ShouldBeTrue();
        LegacyCanonicalDrawMapping[] mappings = fixture.Publisher.LegacyMappings.ToArray();
        AdvancedDrawSubmissionRecord[] records = SubmissionRecords(fixture, committed);

        // No consumer pins the publication, so the ring drains it and the
        // unchanged rows are committed again without the whole-scene plan.
        fixture.Publish();

        fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(1);
        fixture.Publisher.LastPublicationCommittedRetainedRows.ShouldBeTrue();
        fixture.Publisher.PublicationRejected.ShouldBeFalse();
        AdvancedGpuScenePublicationReference recommitted = fixture.Publisher.CurrentPublication;
        recommitted.Sequence.ShouldBeGreaterThan(committed.Sequence);
        fixture.Publisher.TryGetCanonicalHandles(0u, out AdvancedGpuHandle reusedDraw, out _, out _)
            .ShouldBeTrue();
        reusedDraw.ShouldBe(draw);
        fixture.Publisher.LegacyMappings.ToArray().ShouldBe(mappings);
        SubmissionRecords(fixture, recommitted).ShouldBe(records);
        fixture.Publisher.TryGetCanonicalCompatibilityReason(command, 0, out _).ShouldBeFalse();
        AdvancedGpuSceneDrawIdentitySnapshot snapshot = AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);
        snapshot.Publication.ShouldBe(recommitted);
        snapshot.Primary.Handle.ShouldBe(draw);
    }

    [Test]
    public void PinnedUnchangedPublicationIsReusedAndIdentityIsRefreshed()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D command);
        PublishUntilGated(fixture);
        AdvancedGpuScenePublicationReference publication = fixture.Publisher.CurrentPublication;
        fixture.Publisher.Database.TryAcquirePublicationLease(
            in publication, EAdvancedGpuScenePublicationPinKind.Package,
            out AdvancedGpuScenePublicationLease lease).ShouldBeTrue();
        using (lease)
        {
            fixture.Publish();
            fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(1);
            fixture.Publisher.LastPublicationCommittedRetainedRows.ShouldBeFalse();
            fixture.Publisher.CurrentPublication.ShouldBe(publication);

            // A gated reuse still delivers the identity, so a cleared snapshot is repaired.
            ClearCommandIdentitySnapshot(command);
            fixture.Publish();
            fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(1);
            fixture.Publisher.CurrentPublication.ShouldBe(publication);
            AdvancedGpuSceneDrawIdentitySnapshot snapshot = AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);
            snapshot.IsValid.ShouldBeTrue();
            snapshot.Publication.ShouldBe(publication);
        }
    }

    [TestCase("MaterialValue")]
    [TestCase("CullMode")]
    [TestCase("Geometry")]
    [TestCase("WorldMatrix")]
    [TestCase("MeshSwap")]
    [TestCase("MaterialHeader")]
    public void ChangedRowInputRunsWholeScenePlan(string change)
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D command);
        PublishUntilGated(fixture);
        AdvancedGpuScenePublicationReference before = fixture.Publisher.CurrentPublication;
        XRMeshRenderer renderer = command.Mesh!;
        XRMaterial material = renderer.Submeshes[0].Material!;

        switch (change)
        {
            case "MaterialValue":
                material.Transmission = 0.5f;
                break;
            case "CullMode":
                material.RenderOptions.CullMode = ECullMode.None;
                break;
            case "Geometry":
                renderer.Submeshes[0].Mesh!.MarkGeometryChanged();
                break;
            case "WorldMatrix":
                command.WorldMatrixIsModelMatrix = true;
                command.WorldMatrix = Matrix4x4.CreateTranslation(2f, 0f, 0f);
                break;
            case "MeshSwap":
                renderer.Submeshes[0].Mesh = XRMesh.CreateTriangles(
                    Vector3.Zero, Vector3.UnitY, Vector3.UnitZ);
                break;
            case "MaterialHeader":
                // Billboarding changes the material header without a material version.
                material.BillboardMode = EMeshBillboardMode.Perspective;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }

        fixture.Publish();
        fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(0);
        fixture.Publisher.PublicationRejected.ShouldBeFalse(fixture.Publisher.LastPublicationFailure);
        fixture.Publisher.CurrentPublication.Sequence.ShouldBeGreaterThan(before.Sequence);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(command).Publication
            .ShouldBe(fixture.Publisher.CurrentPublication);

        PublishUntilGated(fixture);
    }

    [Test]
    public void FrameGapRunsWholeScenePlanAndCommitsTemporalEvent()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out _);
        PublishUntilGated(fixture);
        AdvancedGpuScenePublicationReference before = fixture.Publisher.CurrentPublication;

        fixture.PublishAfterFrameGap();

        fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(0);
        fixture.Publisher.PublicationRejected.ShouldBeFalse();
        fixture.Publisher.CurrentPublication.Sequence.ShouldBeGreaterThan(before.Sequence);
        fixture.Publisher.Database.TryGetPublicationSnapshot(
            fixture.Publisher.CurrentPublication,
            out AdvancedGpuScenePublicationSnapshot snapshot).ShouldBeTrue();
        snapshot.Submission.Records.Length.ShouldBe(1);
        snapshot.Submission.Records[0].TemporalEventReason
            .ShouldBe(EAdvancedVelocityValidityReason.FrameGap);
        PublishUntilGated(fixture);
    }

    [Test]
    public void CommandMembershipChangeRunsWholeScenePlan()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        RenderInfo3D removedInfo = fixture.AddSource(out RenderCommandMesh3D removed);
        fixture.AddSource(out _);
        PublishUntilGated(fixture);
        fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(2);

        fixture.Scene.Remove(removedInfo);
        fixture.Publish();
        fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(0);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(removed).Primary.Handle.IsValid
            .ShouldBeFalse();
        PublishUntilGated(fixture);
        fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(1);

        fixture.AddSource(out RenderCommandMesh3D added);
        fixture.Publish();
        fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(0);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(added).IsValid.ShouldBeTrue();
        PublishUntilGated(fixture);
        fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(2);
    }

    [Test]
    public void UnsupportedRowStaysUnsupportedThroughGate()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        RenderInfo3D unsupportedInfo = fixture.AddSource(out RenderCommandMesh3D unsupported);
        fixture.Scene.Remove(unsupportedInfo);
        unsupported.RenderPass = (int)EDefaultRenderPass.Background;
        fixture.Scene.Add(unsupportedInfo);
        fixture.AddSource(out RenderCommandMesh3D supported);

        PublishUntilGated(fixture);

        fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(2);
        fixture.Publisher.LegacyMappings.Length.ShouldBe(1);
        fixture.Publisher.TryGetCanonicalCompatibilityReason(unsupported, 0,
            out EAdvancedCanonicalCompatibilityReason reason).ShouldBeTrue();
        reason.ShouldBe(EAdvancedCanonicalCompatibilityReason.UnsupportedRenderPass);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(unsupported).Primary.Handle.IsValid
            .ShouldBeFalse();
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(supported).IsValid.ShouldBeTrue();
    }

    [Test]
    public void GlobalResourceChangeCommitsRetainedRowsWithNewGlobalRows()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D command);
        object lightSource = new();
        AdvancedLightRecord light = new()
        {
            Type = EAdvancedLightType.Directional,
            ColorAndIntensity = new Vector4(1f, 1f, 1f, 1f),
        };
        for (int publication = 0; publication < 3; ++publication)
            fixture.PublishWithLight(lightSource, light);
        fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(1);

        // A consumer pin keeps the publication, so only a global change commits.
        AdvancedGpuScenePublicationReference before = fixture.Publisher.CurrentPublication;
        fixture.Publisher.Database.TryAcquirePublicationLease(
            in before, EAdvancedGpuScenePublicationPinKind.Package,
            out AdvancedGpuScenePublicationLease lease).ShouldBeTrue();
        using (lease)
        {
            LegacyCanonicalDrawMapping[] mappings = fixture.Publisher.LegacyMappings.ToArray();
            fixture.Publisher.Database.TryGetPublicationSnapshot(
                before,
                out AdvancedGpuScenePublicationSnapshot beforeSnapshot).ShouldBeTrue();
            AdvancedDrawSubmissionRecord[] records = beforeSnapshot.Submission.Records.ToArray();
            AdvancedManagedDeformationSourceRow[] sources =
                beforeSnapshot.Submission.DeformationSources.ToArray();
            AdvancedGpuHandle draw = AdvancedGpuSceneIdentityCacheFixture.Snapshot(command).Primary.Handle;

            fixture.PublishWithLight(lightSource, light);
            fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(1);
            fixture.Publisher.LastPublicationCommittedRetainedRows.ShouldBeFalse();
            fixture.Publisher.CurrentPublication.ShouldBe(before);

            light.ColorAndIntensity = new Vector4(1f, 1f, 1f, 2f);
            fixture.PublishWithLight(lightSource, light);

            fixture.Publisher.PublicationRejected.ShouldBeFalse(fixture.Publisher.LastPublicationFailure);
            fixture.Publisher.LastUnchangedRowGateHitCount.ShouldBe(1);
            fixture.Publisher.LastPublicationCommittedRetainedRows.ShouldBeTrue();
            AdvancedGpuScenePublicationReference after = fixture.Publisher.CurrentPublication;
            after.Sequence.ShouldBeGreaterThan(before.Sequence);
            after.Publication.TopologyGeneration.ShouldBe(before.Publication.TopologyGeneration);
            after.Publication.ContentGeneration.ShouldBe(before.Publication.ContentGeneration);
            fixture.Publisher.LegacyMappings.ToArray().ShouldBe(mappings);
            fixture.Publisher.Database.TryGetPublicationSnapshot(
                after,
                out AdvancedGpuScenePublicationSnapshot afterSnapshot).ShouldBeTrue();
            afterSnapshot.Submission.Records.ToArray().ShouldBe(records);
            afterSnapshot.Submission.DeformationSources.ToArray().ShouldBe(sources);
            AdvancedGpuSceneDrawIdentitySnapshot identity = AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);
            identity.Publication.ShouldBe(after);
            identity.Primary.Handle.ShouldBe(draw);
        }
    }

    private static AdvancedDrawSubmissionRecord[] SubmissionRecords(
        AdvancedGpuSceneIdentityCacheFixture fixture,
        in AdvancedGpuScenePublicationReference publication)
    {
        fixture.Publisher.Database.TryGetPublicationSnapshot(
            publication,
            out AdvancedGpuScenePublicationSnapshot snapshot).ShouldBeTrue();
        return snapshot.Submission.Records.ToArray();
    }

    /// <summary>Publishes until the gate reuses the rows. A full plan rebuilds the baseline first.</summary>
    private static void PublishUntilGated(AdvancedGpuSceneIdentityCacheFixture fixture)
    {
        for (int attempt = 0; attempt < MaximumRecoveryPublications; ++attempt)
        {
            fixture.Publish();
            fixture.Publisher.PublicationRejected.ShouldBeFalse(fixture.Publisher.LastPublicationFailure);
            if (fixture.Publisher.LastUnchangedRowGateHitCount != 0)
                return;
        }

        Assert.Fail($"The unchanged-row gate did not pass within {MaximumRecoveryPublications} publications.");
    }

    private static void ClearCommandIdentitySnapshot(RenderCommandMesh3D command)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(RenderCommandMesh3D).GetField("_canonicalDrawIdentitySnapshot", flags)!
            .SetValue(command, default(AdvancedGpuSceneDrawIdentitySnapshot));
        typeof(RenderCommandMesh3D).GetField("_renderCanonicalDrawIdentitySnapshot", flags)!
            .SetValue(command, default(AdvancedGpuSceneDrawIdentitySnapshot));
    }
}
