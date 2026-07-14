using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering.Occlusion;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class CpuOcclusionValidationEvidenceTests
{
    [SetUp]
    public void SetUp()
        => CpuOcclusionValidationEvidence.ResetForTests();

    [Test]
    public void EvidenceRing_UpsertsNamedMembershipAndFullCoverageProofByViewKey()
    {
        const ulong frameId = 71UL;
        var desktop = new OcclusionViewKey(
            renderPass: 3,
            scope: EOcclusionViewScope.EditorDesktopWhileVr,
            pipelineInstanceId: 101,
            povId: 101,
            coverageMask: 0x1u,
            requiredCoverageMask: 0x1u,
            outputId: 0xD001UL);
        var sps = new OcclusionViewKey(
            renderPass: 3,
            scope: EOcclusionViewScope.VrSinglePassStereo,
            pipelineInstanceId: 202,
            povId: -202,
            coverageMask: 0x3u,
            requiredCoverageMask: 0x3u,
            declaredViewCount: 2,
            outputId: 0xD002UL);

        CpuOcclusionValidationEvidence.RecordCandidateForTests(
            frameId,
            desktop,
            11u,
            ECpuOcclusionValidationRole.StableVisibleSentinel,
            EOcclusionCullingMode.CpuQueryAsync);
        CpuOcclusionValidationEvidence.RecordOutcomeForTests(
            frameId,
            desktop,
            11u,
            ECpuOcclusionValidationRole.StableVisibleSentinel,
            EOcclusionCullingMode.CpuQueryAsync,
            rendered: true,
            culled: false,
            proofCoverageMask: 0u,
            decision: ECpuOcclusionDecision.Visible);
        CpuOcclusionValidationEvidence.RecordCandidateForTests(
            frameId,
            sps,
            12u,
            ECpuOcclusionValidationRole.HiddenTarget,
            EOcclusionCullingMode.CpuQueryAsync);
        CpuOcclusionValidationEvidence.RecordOutcomeForTests(
            frameId,
            sps,
            12u,
            ECpuOcclusionValidationRole.HiddenTarget,
            EOcclusionCullingMode.CpuQueryAsync,
            rendered: false,
            culled: true,
            proofCoverageMask: 0x3u,
            decision: ECpuOcclusionDecision.Skip);

        Span<CpuOcclusionValidationEvidenceSnapshot> snapshots =
            stackalloc CpuOcclusionValidationEvidenceSnapshot[CpuOcclusionValidationEvidence.MaximumEntriesPerFrame];
        int count = CpuOcclusionValidationEvidence.CopyFrame(frameId, snapshots);

        count.ShouldBe(2);
        CpuOcclusionValidationEvidenceSnapshot sentinel = snapshots[0];
        sentinel.ViewKey.ShouldBe(desktop);
        sentinel.Role.ShouldBe(ECpuOcclusionValidationRole.StableVisibleSentinel);
        sentinel.CandidateObserved.ShouldBeTrue();
        sentinel.Rendered.ShouldBeTrue();
        sentinel.Culled.ShouldBeFalse();

        CpuOcclusionValidationEvidenceSnapshot hidden = snapshots[1];
        hidden.ViewKey.ShouldBe(sps);
        hidden.Role.ShouldBe(ECpuOcclusionValidationRole.HiddenTarget);
        hidden.CandidateObserved.ShouldBeTrue();
        hidden.Rendered.ShouldBeFalse();
        hidden.Culled.ShouldBeTrue();
        hidden.OcclusionProofCoverageMask.ShouldBe(0x3u);
        hidden.ViewKey.OutputId.ShouldBe(0xD002UL);
    }

    [Test]
    public void EvidenceRing_IsBoundedAndReportsOverflow()
    {
        const ulong frameId = 81UL;
        var key = new OcclusionViewKey(
            renderPass: 3,
            scope: EOcclusionViewScope.MonoDesktop,
            pipelineInstanceId: 303,
            povId: 303,
            outputId: 0xD003UL);

        for (uint i = 0; i < CpuOcclusionValidationEvidence.MaximumEntriesPerFrame + 5u; i++)
        {
            CpuOcclusionValidationEvidence.RecordCandidateForTests(
                frameId,
                key,
                i + 1u,
                ECpuOcclusionValidationRole.HiddenTarget,
                EOcclusionCullingMode.CpuQueryAsync);
        }

        Span<CpuOcclusionValidationEvidenceSnapshot> snapshots =
            stackalloc CpuOcclusionValidationEvidenceSnapshot[CpuOcclusionValidationEvidence.MaximumEntriesPerFrame];
        CpuOcclusionValidationEvidence.CopyFrame(frameId, snapshots)
            .ShouldBe(CpuOcclusionValidationEvidence.MaximumEntriesPerFrame);
        CpuOcclusionValidationEvidence.OverflowCount.ShouldBe(5L);
    }

    [TestCase(
        CpuOcclusionValidationEvidence.DesktopMovingSentinelMaterialName,
        ECpuOcclusionValidationRole.MovingVisibleSentinel)]
    [TestCase(
        CpuOcclusionValidationEvidence.DesktopTopEdgeSentinelMaterialName,
        ECpuOcclusionValidationRole.TopEdgeVisibleSentinel)]
    [TestCase(
        CpuOcclusionValidationEvidence.SpsMovingSentinelMaterialName,
        ECpuOcclusionValidationRole.MovingVisibleSentinel)]
    [TestCase(
        CpuOcclusionValidationEvidence.SpsTopEdgeSentinelMaterialName,
        ECpuOcclusionValidationRole.TopEdgeVisibleSentinel)]
    public void ScopeSpecificMaterials_MapToGenericValidationRoles(
        string materialName,
        ECpuOcclusionValidationRole expectedRole)
        => CpuOcclusionValidationEvidence.ResolveRole(materialName).ShouldBe(expectedRole);

    [Test]
    public void ScopeSpecificMaterials_AreOnlyApplicableToTheirOwningOutput()
    {
        CpuOcclusionValidationEvidence.IsApplicableToScope(
                CpuOcclusionValidationEvidence.DesktopMovingSentinelMaterialName,
                EOcclusionViewScope.EditorDesktopWhileVr)
            .ShouldBeTrue();
        CpuOcclusionValidationEvidence.IsApplicableToScope(
                CpuOcclusionValidationEvidence.DesktopMovingSentinelMaterialName,
                EOcclusionViewScope.VrSinglePassStereo)
            .ShouldBeFalse();
        CpuOcclusionValidationEvidence.IsApplicableToScope(
                CpuOcclusionValidationEvidence.SpsMovingSentinelMaterialName,
                EOcclusionViewScope.VrSinglePassStereo)
            .ShouldBeTrue();
        CpuOcclusionValidationEvidence.IsApplicableToScope(
                CpuOcclusionValidationEvidence.SpsMovingSentinelMaterialName,
                EOcclusionViewScope.EditorDesktopWhileVr)
            .ShouldBeFalse();
    }

    [Test]
    public void ScopeSpecificSentinels_RenderOnlyInTheirOwningEvidenceScope()
    {
        ECpuOcclusionValidationRole desktopRole = CpuOcclusionValidationEvidence.ResolveRole(
            CpuOcclusionValidationEvidence.DesktopMovingSentinelMaterialName);
        ECpuOcclusionValidationRole spsRole = CpuOcclusionValidationEvidence.ResolveRole(
            CpuOcclusionValidationEvidence.SpsTopEdgeSentinelMaterialName);

        CpuOcclusionValidationEvidence.IsApplicableToScope(
            CpuOcclusionValidationEvidence.DesktopMovingSentinelMaterialName,
            EOcclusionViewScope.VrSinglePassStereo).ShouldBeFalse();
        CpuOcclusionValidationEvidence.IsApplicableToScope(
            CpuOcclusionValidationEvidence.SpsTopEdgeSentinelMaterialName,
            EOcclusionViewScope.EditorDesktopWhileVr).ShouldBeFalse();

        CpuOcclusionValidationEvidence.IsKnownVisibleRole(desktopRole).ShouldBeTrue();
        CpuOcclusionValidationEvidence.IsKnownVisibleRole(spsRole).ShouldBeTrue();
        CpuOcclusionValidationEvidence.ShouldRenderInScope(desktopRole, scopeApplicable: false).ShouldBeFalse();
        CpuOcclusionValidationEvidence.ShouldRenderInScope(spsRole, scopeApplicable: false).ShouldBeFalse();
        CpuOcclusionValidationEvidence.ShouldRenderInScope(desktopRole, scopeApplicable: true).ShouldBeTrue();
        CpuOcclusionValidationEvidence.ShouldRenderInScope(ECpuOcclusionValidationRole.None, scopeApplicable: false).ShouldBeTrue();
    }

    [Test]
    public void EvidenceRing_RecordsUnlabelledCandidatesWithoutChangingTheirDecision()
    {
        const ulong frameId = 91UL;
        var key = new OcclusionViewKey(
            renderPass: 3,
            scope: EOcclusionViewScope.MonoDesktop,
            pipelineInstanceId: 404,
            povId: 404,
            outputId: 0xD004UL);

        CpuOcclusionValidationEvidence.RecordCandidateForTests(
            frameId,
            key,
            21u,
            ECpuOcclusionValidationRole.None,
            EOcclusionCullingMode.CpuQueryAsync);
        CpuOcclusionValidationEvidence.RecordOutcomeForTests(
            frameId,
            key,
            21u,
            ECpuOcclusionValidationRole.None,
            EOcclusionCullingMode.CpuQueryAsync,
            rendered: false,
            culled: true,
            proofCoverageMask: 0x1u,
            decision: ECpuOcclusionDecision.Skip);

        Span<CpuOcclusionValidationEvidenceSnapshot> snapshots =
            stackalloc CpuOcclusionValidationEvidenceSnapshot[1];
        CpuOcclusionValidationEvidence.CopyFrame(frameId, snapshots).ShouldBe(1);
        snapshots[0].Role.ShouldBe(ECpuOcclusionValidationRole.None);
        snapshots[0].Decision.ShouldBe(ECpuOcclusionDecision.Skip);
        snapshots[0].Culled.ShouldBeTrue();
    }
}
