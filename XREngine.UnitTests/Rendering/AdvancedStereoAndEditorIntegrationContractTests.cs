using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedStereoAndEditorIntegrationContractTests
{
    [Test]
    public void StereoContract_LayerResolutionAndEyeIsolation()
    {
        AdvancedStereoContract.ResolveLayerCount(EAdvancedStereoMode.Mono).ShouldBe(1u);
        AdvancedStereoContract.ResolveLayerCount(EAdvancedStereoMode.RvcTwoPass).ShouldBe(2u);
        AdvancedStereoContract.ResolveLayerCount(EAdvancedStereoMode.OpenGlSinglePassStereo).ShouldBe(2u);
        AdvancedStereoContract.ResolveLayerCount(EAdvancedStereoMode.VulkanMultiview, viewCount: 4u).ShouldBe(4u);

        // Strict eye isolation: never share occlusion verdicts between different eyes
        AdvancedStereoContract.CanShareOcclusionVerdict(0u, 0u).ShouldBeTrue();
        AdvancedStereoContract.CanShareOcclusionVerdict(1u, 1u).ShouldBeTrue();
        AdvancedStereoContract.CanShareOcclusionVerdict(0u, 1u).ShouldBeFalse();
        AdvancedStereoContract.CanShareOcclusionVerdict(1u, 0u).ShouldBeFalse();
    }

    [Test]
    public void FoveationContract_ConservativeLODBiasCalculations()
    {
        // Inside fovea center (inner radius <= 0.25), bias is zero
        AdvancedFoveationContract.CalculateConservativeLODBias(0.1f).ShouldBe(0.0f);
        AdvancedFoveationContract.CalculateConservativeLODBias(0.25f).ShouldBe(0.0f);

        // In periphery, bias increases to pull towards higher MIPs
        float midBias = AdvancedFoveationContract.CalculateConservativeLODBias(0.6f);
        float outerBias = AdvancedFoveationContract.CalculateConservativeLODBias(0.9f);
        midBias.ShouldBeGreaterThan(0.0f);
        outerBias.ShouldBeGreaterThan(midBias);
    }

    [Test]
    public void OpenXrTimingContract_TimingAndLateLatchAcceptance()
    {
        AdvancedOpenXrTimingContract.IsPredictedDisplayTimeValid(predictedDisplayTimeNs: 2000, currentFrameTimeNs: 1000).ShouldBeTrue();
        AdvancedOpenXrTimingContract.IsPredictedDisplayTimeValid(predictedDisplayTimeNs: 1000, currentFrameTimeNs: 2000).ShouldBeFalse();

        AdvancedOpenXrTimingContract.ShouldApplyLateLatch(isLateLatchSupported: true, isCameraCut: false).ShouldBeTrue();
        AdvancedOpenXrTimingContract.ShouldApplyLateLatch(isLateLatchSupported: true, isCameraCut: true).ShouldBeFalse();
        AdvancedOpenXrTimingContract.ShouldApplyLateLatch(isLateLatchSupported: false, isCameraCut: false).ShouldBeFalse();
    }

    [Test]
    public void OffscreenProfile_ConfiguresCapabilities()
    {
        AdvancedOffscreenProfile thumb = AdvancedOffscreenProfile.ForThumbnail();
        thumb.EnablePostProcessing.ShouldBeFalse();
        thumb.EnableTemporalHistory.ShouldBeFalse();
        thumb.EnableLateTransparency.ShouldBeFalse();

        AdvancedOffscreenProfile mirror = AdvancedOffscreenProfile.ForMirror();
        mirror.EnablePostProcessing.ShouldBeFalse();
        mirror.EnableLateTransparency.ShouldBeTrue();
        mirror.EnableTemporalHistory.ShouldBeFalse();
    }

    [Test]
    public void PickingContract_QueryLayoutAndResolvedResultIdentity()
    {
        Unsafe.SizeOf<AdvancedPickingQuery>().ShouldBe(12);

        AdvancedPickingContract.IsInBounds(50, 50, 100, 100).ShouldBeTrue();
        AdvancedPickingContract.IsInBounds(100, 50, 100, 100).ShouldBeFalse();

        AdvancedPickingResult miss = AdvancedPickingResult.Miss(
            requestGeneration: 10u,
            databaseEpoch: 20u,
            publicationSequence: 30u,
            viewIndex: 1u);
        miss.IsHit.ShouldBeFalse();
        miss.RequestGeneration.ShouldBe(10u);
        miss.Draw.ShouldBe(AdvancedGpuHandle.Invalid);

        AdvancedPickingResult hit = new(
            IsHit: true,
            RequestGeneration: 10u,
            DatabaseEpoch: 20u,
            PublicationSequence: 30u,
            Draw: new AdvancedGpuHandle(42u, 2u),
            Instance: new AdvancedGpuHandle(1024u, 3u),
            Geometry: new AdvancedGpuHandle(4u, 1u),
            Material: new AdvancedGpuHandle(5u, 1u),
            CurrentTransform: new AdvancedGpuHandle(6u, 1u),
            PreviousTransform: new AdvancedGpuHandle(7u, 1u),
            EditorIdentity: new AdvancedGpuHandle(8u, 1u),
            StableComponentId: 9u,
            LogicalMeshId: 10u,
            SelectionId: 3u,
            PrimitiveSection: 2u,
            Producer: EAdvancedGeometryProducer.IndirectIndexed,
            PrimitiveId: 7u,
            MeshletOrClusterId: 11u,
            LocalPrimitiveId: 12u,
            ViewIndex: 1u,
            AuthoringRenderInfo: null);
        hit.IsHit.ShouldBeTrue();
        hit.Draw.ShouldBe(new AdvancedGpuHandle(42u, 2u));
        hit.PrimitiveId.ShouldBe(7u);
        hit.Instance.ShouldBe(new AdvancedGpuHandle(1024u, 3u));
        hit.SelectionId.ShouldBe(3u);
    }

    [Test]
    public void DiagnosticsContract_CaptureNamesAndMcpReport()
    {
        AdvancedDiagnosticsContract.CaptureVisibilityIdentity.ShouldBe("Capture.Advanced.VisibilityIdentity");
        AdvancedDiagnosticsContract.CaptureOpaqueHdr.ShouldBe("Capture.Advanced.OpaqueHdr");

        var report = AdvancedDiagnosticsContract.BuildMcpDiagnosticReport(
            EAdvancedStereoMode.VulkanMultiview,
            viewCount: 2u,
            isFoveationEnabled: true,
            activeGIMode: "LightProbesAndIbl",
            activeAOProvider: "MockGTAO");

        report["pipeline"].ShouldBe("AdvancedRenderPipeline");
        report["stereoMode"].ShouldBe("VulkanMultiview");
        report["viewCount"].ShouldBe(2u);
        report["foveationEnabled"].ShouldBe(true);
        report["ambientOcclusion"].ShouldBe("MockGTAO");
    }
}
