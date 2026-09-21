using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class DDGIScaffoldingContractTests
{
    [Test]
    public void DefaultRenderPipeline_UsesDDGI_ReflectsGlobalIlluminationMode()
    {
        var pipeline = new DefaultRenderPipeline();
        IGlobalIlluminationPipelineProvider provider = pipeline;

        pipeline.GlobalIlluminationMode = EGlobalIlluminationMode.DDGI;
        provider.UsesDDGI.ShouldBeTrue();
        provider.UsesLightProbeGI.ShouldBeFalse();
        provider.UsesSurfelGI.ShouldBeFalse();
        provider.UsesRestirGI.ShouldBeFalse();
        provider.UsesRadianceCascades.ShouldBeFalse();
        provider.UsesLightVolumes.ShouldBeFalse();
        provider.UsesVoxelConeTracing.ShouldBeFalse();

        pipeline.GlobalIlluminationMode = EGlobalIlluminationMode.LightProbesAndIbl;
        provider.UsesDDGI.ShouldBeFalse();
        provider.UsesLightProbeGI.ShouldBeTrue();

        pipeline.GlobalIlluminationMode = EGlobalIlluminationMode.SurfelGI;
        provider.UsesDDGI.ShouldBeFalse();
        provider.UsesSurfelGI.ShouldBeTrue();
    }

    [Test]
    public void AdvancedRenderPipeline_UsesDDGI_ReflectsGlobalIlluminationMode()
    {
        var pipeline = new AdvancedRenderPipeline();
        IGlobalIlluminationPipelineProvider provider = pipeline;

        pipeline.GlobalIlluminationMode = EGlobalIlluminationMode.DDGI;
        provider.UsesDDGI.ShouldBeTrue();
        provider.UsesLightProbeGI.ShouldBeFalse();

        pipeline.GlobalIlluminationMode = EGlobalIlluminationMode.LightProbesAndIbl;
        provider.UsesDDGI.ShouldBeFalse();
        provider.UsesLightProbeGI.ShouldBeTrue();
    }

    [Test]
    public void DefaultPipelineResourceFeature_DdgiResourcesEnabled_IsExpectedMask()
    {
        DefaultRenderPipeline.DefaultPipelineResourceFeature.DdgiResourcesEnabled.ShouldBe(
            (DefaultRenderPipeline.DefaultPipelineResourceFeature)(1UL << 31));
    }

    [Test]
    public void DDGIResourceNames_AreConsistentAcrossPipelines()
    {
        DefaultRenderPipeline.DDGITextureName.ShouldBe("DDGITexture");
        DefaultRenderPipeline.DDGICompositeFBOName.ShouldBe("DDGICompositeFBO");
        DefaultRenderPipeline.DDGIIrradianceAtlasTextureName.ShouldBe("DDGIIrradianceAtlas");
        DefaultRenderPipeline.DDGIVisibilityAtlasTextureName.ShouldBe("DDGIVisibilityAtlas");
        DefaultRenderPipeline.DDGIProbeStateBufferName.ShouldBe("DDGIProbeStateBuffer");
        DefaultRenderPipeline.DDGIRayBufferName.ShouldBe("DDGIRayBuffer");
        DefaultRenderPipeline.DDGIHitBufferName.ShouldBe("DDGIHitBuffer");

        AdvancedRenderPipeline.DDGITextureName.ShouldBe(DefaultRenderPipeline.DDGITextureName);
        AdvancedRenderPipeline.DDGICompositeFBOName.ShouldBe(DefaultRenderPipeline.DDGICompositeFBOName);
        AdvancedRenderPipeline.DDGIIrradianceAtlasTextureName.ShouldBe(DefaultRenderPipeline.DDGIIrradianceAtlasTextureName);
        AdvancedRenderPipeline.DDGIVisibilityAtlasTextureName.ShouldBe(DefaultRenderPipeline.DDGIVisibilityAtlasTextureName);
        AdvancedRenderPipeline.DDGIProbeStateBufferName.ShouldBe(DefaultRenderPipeline.DDGIProbeStateBufferName);
        AdvancedRenderPipeline.DDGIRayBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayBufferName);
        AdvancedRenderPipeline.DDGIHitBufferName.ShouldBe(DefaultRenderPipeline.DDGIHitBufferName);
    }

    [Test]
    public void DDGIVolumeComponent_DefaultProperties_AreInitializedCorrectly()
    {
        var volume = new DDGIVolumeComponent();

        volume.HalfExtents.ShouldBe(new Vector3(10f, 6f, 10f));
        volume.ProbeCounts.ShouldBe(new IVector3(16, 8, 16));
        volume.TotalProbeCount.ShouldBe(16 * 8 * 16);
        volume.RaysPerProbe.ShouldBe(128);
        volume.MaxProbesUpdatedPerFrame.ShouldBe(0);
        volume.Hysteresis.ShouldBe(0.97f);
        volume.NormalBias.ShouldBe(0.1f);
        volume.ViewBias.ShouldBe(0.2f);
        volume.ChebyshevPower.ShouldBe(4.0f);
        volume.RelocationEnabled.ShouldBeTrue();
        volume.ClassificationEnabled.ShouldBeTrue();
        volume.BakedMode.ShouldBeFalse();
        volume.DebugDrawProbes.ShouldBeFalse();
        volume.VolumeEnabled.ShouldBeTrue();
        volume.Intensity.ShouldBe(1.0f);
    }

    [Test]
    public void DDGIVolumeComponent_ProbeSpacing_ComputesCorrectly()
    {
        var volume = new DDGIVolumeComponent
        {
            HalfExtents = new Vector3(15f, 7f, 15f),
            ProbeCounts = new IVector3(16, 8, 16)
        };

        // Extents: 30, 14, 30. Counts - 1: 15, 7, 15. Spacing: 2, 2, 2.
        Vector3 spacing = volume.ProbeSpacing;
        spacing.X.ShouldBe(2.0f, 0.0001f);
        spacing.Y.ShouldBe(2.0f, 0.0001f);
        spacing.Z.ShouldBe(2.0f, 0.0001f);
    }

    [Test]
    public void DDGIGPUStructs_HaveExpectedSizesAndStd430Alignment()
    {
        System.Runtime.InteropServices.Marshal.SizeOf<XREngine.Rendering.GI.DDGI.DDGIProbeGPU>().ShouldBe(32);
        System.Runtime.InteropServices.Marshal.SizeOf<XREngine.Rendering.GI.DDGI.DDGIRayGPU>().ShouldBe(48);
        System.Runtime.InteropServices.Marshal.SizeOf<XREngine.Rendering.GI.DDGI.DDGIHitGPU>().ShouldBe(32);
        System.Runtime.InteropServices.Marshal.SizeOf<XREngine.Rendering.GI.DDGI.DDGIConstantsGPU>().ShouldBe(96);
    }

    [Test]
    public void DDGIVolumeRuntimeState_AtlasDimensionCalculations_MatchSpecification()
    {
        // 16 probes in X, 8 texels (6 + 2 border) -> 128 texels wide
        int irrWidth = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeAtlasWidth(16, 8);
        irrWidth.ShouldBe(128);

        // 8 probes in Y * 16 probes in Z = 128 rows of probes, 8 texels each -> 1024 texels high
        int irrHeight = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeAtlasHeight(8, 16, 8);
        irrHeight.ShouldBe(1024);

        // Visibility atlas: 16 probes in X, 18 texels (16 + 2 border) -> 288 texels wide
        int visWidth = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeAtlasWidth(16, 18);
        visWidth.ShouldBe(288);

        // 8 probes in Y * 16 probes in Z = 128 rows of probes, 18 texels each -> 2304 texels high
        int visHeight = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeAtlasHeight(8, 16, 18);
        visHeight.ShouldBe(2304);
    }

    [Test]
    public void DDGIVolumeRuntimeState_AtlasTileIndexing_RoundTripsAccurately()
    {
        int probeCountX = 16;
        int totalProbes = 16 * 8 * 16; // 2048

        for (int i = 0; i < totalProbes; i += 37) // Sample across grid
        {
            var (tx, ty) = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeTileCoord(i, probeCountX);
            int reconstructed = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeProbeIndex(tx, ty, probeCountX);
            reconstructed.ShouldBe(i);
        }
    }

    [Test]
    public void DDGIVolumeRuntimeState_ProbeGridCoordinate_RoundTripsAccurately()
    {
        var counts = new IVector3(16, 8, 16);
        int totalProbes = counts.X * counts.Y * counts.Z;

        for (int i = 0; i < totalProbes; i += 41)
        {
            IVector3 gc = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeProbeGridCoord(i, counts);
            int reconstructed = gc.X + gc.Y * counts.X + gc.Z * (counts.X * counts.Y);
            reconstructed.ShouldBe(i);
        }
    }

    [Test]
    public void DDGIVolumeRuntimeState_ProbeWorldPosition_ComputesExtremesCorrectly()
    {
        var counts = new IVector3(16, 8, 16);
        var halfExtents = new Vector3(15f, 7f, 15f);
        var origin = Vector3.Zero;
        var gridMin = origin - halfExtents;
        var spacing = new Vector3(2.0f, 2.0f, 2.0f);

        // Probe 0 is at (0, 0, 0)
        Vector3 p0 = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeProbeWorldPosition(0, gridMin, spacing, counts);
        p0.ShouldBe(gridMin);

        // Last probe is at (15, 7, 15)
        int lastIndex = counts.X * counts.Y * counts.Z - 1;
        Vector3 pLast = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeProbeWorldPosition(lastIndex, gridMin, spacing, counts);
        var gridMax = origin + halfExtents;
        pLast.X.ShouldBe(gridMax.X, 0.001f);
        pLast.Y.ShouldBe(gridMax.Y, 0.001f);
        pLast.Z.ShouldBe(gridMax.Z, 0.001f);
    }

    [Test]
    public void DDGIVolumeComponent_RuntimeState_SynchronizesWithAuthoredProperties()
    {
        var volume = new DDGIVolumeComponent
        {
            ProbeCounts = new IVector3(8, 4, 8),
            HalfExtents = new Vector3(8f, 4f, 8f),
            RaysPerProbe = 64,
            MaxProbesUpdatedPerFrame = 128,
            Hysteresis = 0.95f,
            NormalBias = 0.15f,
            ViewBias = 0.25f,
            ChebyshevPower = 8.0f,
            Intensity = 1.5f,
            RelocationEnabled = false,
            ClassificationEnabled = false,
            DebugDrawProbes = true
        };

        var state = volume.RuntimeState;
        state.ShouldNotBeNull();
        state.ProbeCounts.ShouldBe(new IVector3(8, 4, 8));
        state.HalfExtents.ShouldBe(new Vector3(8f, 4f, 8f));
        state.TotalProbeCount.ShouldBe(8 * 4 * 8);
        state.RaysPerProbe.ShouldBe(64);
        state.MaxProbesUpdatedPerFrame.ShouldBe(128);
        state.ActiveRayBudget.ShouldBe(128 * 64);
        state.Hysteresis.ShouldBe(0.95f);
        state.NormalBias.ShouldBe(0.15f);
        state.ViewBias.ShouldBe(0.25f);
        state.ChebyshevPower.ShouldBe(8.0f);
        state.Intensity.ShouldBe(1.5f);
        state.RelocationEnabled.ShouldBeFalse();
        state.ClassificationEnabled.ShouldBeFalse();
        state.DebugDrawProbes.ShouldBeTrue();
    }

    [Test]
    public void DDGIHitGPU_Layout_MatchesBvhRaycastHitRecordContract()
    {
        // 32-byte layout: t(4), objectId(4), faceIndex(4), triangleIndex(4), barycentric(12), padding(4)
        System.Runtime.InteropServices.Marshal.SizeOf<XREngine.Rendering.GI.DDGI.DDGIHitGPU>().ShouldBe(32);
        System.Runtime.InteropServices.Marshal.OffsetOf<XREngine.Rendering.GI.DDGI.DDGIHitGPU>(nameof(XREngine.Rendering.GI.DDGI.DDGIHitGPU.T)).ToInt32().ShouldBe(0);
        System.Runtime.InteropServices.Marshal.OffsetOf<XREngine.Rendering.GI.DDGI.DDGIHitGPU>(nameof(XREngine.Rendering.GI.DDGI.DDGIHitGPU.ObjectId)).ToInt32().ShouldBe(4);
        System.Runtime.InteropServices.Marshal.OffsetOf<XREngine.Rendering.GI.DDGI.DDGIHitGPU>(nameof(XREngine.Rendering.GI.DDGI.DDGIHitGPU.FaceIndex)).ToInt32().ShouldBe(8);
        System.Runtime.InteropServices.Marshal.OffsetOf<XREngine.Rendering.GI.DDGI.DDGIHitGPU>(nameof(XREngine.Rendering.GI.DDGI.DDGIHitGPU.TriangleIndex)).ToInt32().ShouldBe(12);
        System.Runtime.InteropServices.Marshal.OffsetOf<XREngine.Rendering.GI.DDGI.DDGIHitGPU>(nameof(XREngine.Rendering.GI.DDGI.DDGIHitGPU.Barycentric)).ToInt32().ShouldBe(16);
        System.Runtime.InteropServices.Marshal.OffsetOf<XREngine.Rendering.GI.DDGI.DDGIHitGPU>(nameof(XREngine.Rendering.GI.DDGI.DDGIHitGPU.Padding)).ToInt32().ShouldBe(28);
    }

    [Test]
    public void DDGIRaygenShader_Source_ContainsExpectedContracts()
    {
        string source = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_raygen.comp");
        source.ShouldContain("#version 450");
        source.ShouldContain("SphericalFibonacci");
        source.ShouldContain("GOLDEN_ANGLE");
        source.ShouldContain("uRandomRotation");
        source.ShouldContain("DDGIProbeGPU");
        source.ShouldContain("DDGIRayInput");
        source.ShouldContain("gRays[rayIndex].origin =");
        source.ShouldContain("gRays[rayIndex].direction =");
    }

    [Test]
    public void DDGITraceShader_Source_ContainsExpectedContracts()
    {
        string source = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_trace.comp");
        source.ShouldContain("#version 450");
        source.ShouldContain("#define XR_FORCE_CLOSEST_HIT 1u");
        source.ShouldContain("#define XR_CUSTOM_RAY_INPUT 1");
        source.ShouldContain("#pragma snippet \"BvhRaycastCore\"");
        source.ShouldContain("DDGIRayInput");
        source.ShouldContain("TraceRay(ray, uRootIndex, stackLimit, anyHit)");
        source.ShouldContain("gHits[rayIndex] = hit;");
    }

    [Test]
    public void VPRC_BuildAccelerationStructure_TriangleBufferVariableName_IsConfigured()
    {
        var bvhCmd = new XREngine.Rendering.Pipelines.Commands.VPRC_BuildAccelerationStructure();
        bvhCmd.TriangleBufferVariableName.ShouldBe("AccelerationStructureTriangles");
    }

    [Test]
    public void VPRC_DDGIPasses_Defaults_AreConfiguredCorrectly()
    {
        var raygen = new XREngine.Rendering.Pipelines.Commands.VPRC_DDGIRaygenPass();
        raygen.ProbeStateBufferName.ShouldBe(DefaultRenderPipeline.DDGIProbeStateBufferName);
        raygen.RayBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayBufferName);

        var trace = new XREngine.Rendering.Pipelines.Commands.VPRC_DDGITracePass();
        trace.RayBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayBufferName);
        trace.HitBufferName.ShouldBe(DefaultRenderPipeline.DDGIHitBufferName);
        trace.TriangleBufferVariableName.ShouldBe("AccelerationStructureTriangles");

        var debugViz = new XREngine.Rendering.Pipelines.Commands.VPRC_DDGIDebugVisualization();
        debugViz.ReadyVariableName.ShouldBe("AccelerationStructureReady");
        debugViz.NodeCountVariableName.ShouldBe("AccelerationStructureNodeCount");
        debugViz.Enabled.ShouldBeFalse();
    }

    [Test]
    public void SphericalFibonacciDistribution_GeneratesUnitVectors()
    {
        const float goldenAngle = 2.39996322972865332f;
        int numSamples = 128;

        for (int i = 0; i < numSamples; i++)
        {
            float phi = i * goldenAngle;
            float cosTheta = 1.0f - (2.0f * i + 1.0f) / numSamples;
            float sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - cosTheta * cosTheta));
            Vector3 dir = new Vector3(MathF.Cos(phi) * sinTheta, MathF.Sin(phi) * sinTheta, cosTheta);

            float lengthSq = dir.LengthSquared();
            lengthSq.ShouldBe(1.0f, 0.0001f);
        }
    }

    [Test]
    public void DDGIRayRadianceGPU_LayoutAndSize_IsExpected()
    {
        System.Runtime.InteropServices.Marshal.SizeOf<XREngine.Rendering.GI.DDGI.DDGIRayRadianceGPU>().ShouldBe(16);
    }

    [Test]
    public void DDGIRayRadianceBufferName_IsConsistentAcrossPipelines()
    {
        DefaultRenderPipeline.DDGIRayRadianceBufferName.ShouldBe("DDGIRayRadianceBuffer");
        AdvancedRenderPipeline.DDGIRayRadianceBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayRadianceBufferName);
    }

    [Test]
    public void DDGIVolumeRuntimeState_WarmStartAndInvalidation_BehavesCorrectly()
    {
        var state = new XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState();

        // Initial state: frame 0, invalidated -> hysteresis must be 0 for warm start
        state.FrameIndex.ShouldBe(0u);
        state.IsInvalidated.ShouldBeTrue();
        state.ResolveEffectiveHysteresis().ShouldBe(0.0f);

        // Complete first frame: transitions to tracking
        state.OnFrameCompleted();
        state.FrameIndex.ShouldBe(1u);
        state.IsInvalidated.ShouldBeFalse();
        state.ResolveEffectiveHysteresis().ShouldBe(state.Hysteresis);

        // Explicit invalidation: resets hysteresis to 0
        state.Invalidate();
        state.IsInvalidated.ShouldBeTrue();
        state.ResolveEffectiveHysteresis().ShouldBe(0.0f);

        // Complete subsequent frame: returns to tracking
        state.OnFrameCompleted();
        state.FrameIndex.ShouldBe(2u);
        state.IsInvalidated.ShouldBeFalse();
        state.ResolveEffectiveHysteresis().ShouldBe(state.Hysteresis);
    }

    [Test]
    public void OctahedralBorderMapping_CornersAndEdges_MapCorrectly()
    {
        // Test S = 6 (Irradiance: interior is 4x4, border is 1 texel)
        int S = 6;
        static (int x, int y) Map(int x, int y, int size)
        {
            if (x == 0 && y == 0) return (size - 2, size - 2);
            if (x == 0 && y == size - 1) return (size - 2, 1);
            if (x == size - 1 && y == 0) return (1, size - 2);
            if (x == size - 1 && y == size - 1) return (1, 1);

            if (y == 0) return (size - 1 - x, 1);
            if (y == size - 1) return (size - 1 - x, size - 2);
            if (x == 0) return (1, size - 1 - y);
            if (x == size - 1) return (size - 2, size - 1 - y);

            return (x, y);
        }

        // Corners for S = 6:
        Map(0, 0, S).ShouldBe((4, 4));
        Map(0, 5, S).ShouldBe((4, 1));
        Map(5, 0, S).ShouldBe((1, 4));
        Map(5, 5, S).ShouldBe((1, 1));

        // Edges for S = 6:
        Map(1, 0, S).ShouldBe((4, 1));
        Map(2, 0, S).ShouldBe((3, 1));
        Map(3, 0, S).ShouldBe((2, 1));
        Map(4, 0, S).ShouldBe((1, 1));

        // Test S = 16 (Visibility: interior is 14x14)
        int S16 = 16;
        Map(0, 0, S16).ShouldBe((14, 14));
        Map(0, 15, S16).ShouldBe((14, 1));
        Map(15, 0, S16).ShouldBe((1, 14));
        Map(15, 15, S16).ShouldBe((1, 1));
    }

    [Test]
    public void DDGIPhase3_Shaders_ExistAndContainExpectedContracts()
    {
        string hitShade = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_hit_shade.comp");
        hitShade.ShouldContain("#version 450");
        hitShade.ShouldContain("#pragma snippet \"DDGISampling\"");
        hitShade.ShouldContain("uDirLightCount");
        hitShade.ShouldContain("sampleDDGI(hitPos, normal)");
        hitShade.ShouldContain("gRayRadiance[rayIndex] =");

        string updateIrr = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_update_irradiance.comp");
        updateIrr.ShouldContain("#version 450");
        updateIrr.ShouldContain("uIrradianceAtlas");
        updateIrr.ShouldContain("uHysteresis");
        updateIrr.ShouldContain("local_size_x = 6");
        updateIrr.ShouldContain("local_size_y = 6");

        string updateVis = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_update_visibility.comp");
        updateVis.ShouldContain("#version 450");
        updateVis.ShouldContain("uVisibilityAtlas");
        updateVis.ShouldContain("uChebyshevPower");
        updateVis.ShouldContain("local_size_x = 16");
        updateVis.ShouldContain("local_size_y = 16");

        string borderSnippet = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Snippets/OctahedralBorderCopy.glsl");
        borderSnippet.ShouldContain("MapBorderToInterior");
        borderSnippet.ShouldContain("GetBorderLocalCoord");

        string borderCopyIrr = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_border_copy.comp");
        borderCopyIrr.ShouldContain("uIrradianceAtlas");
        borderCopyIrr.ShouldContain("#pragma snippet \"OctahedralBorderCopy\"");

        string borderCopyVis = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_border_copy_visibility.comp");
        borderCopyVis.ShouldContain("uVisibilityAtlas");
        borderCopyVis.ShouldContain("#pragma snippet \"OctahedralBorderCopy\"");

        string samplingSnippet = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Snippets/DDGISampling.glsl");
        samplingSnippet.ShouldContain("sampleDDGI");
        samplingSnippet.ShouldContain("uDDGIIrradianceAtlas");
        samplingSnippet.ShouldContain("uDDGIVisibilityAtlas");
    }

    [Test]
    public void VPRC_Phase3Passes_Defaults_AreConfiguredCorrectly()
    {
        var hitShade = new XREngine.Rendering.Pipelines.Commands.VPRC_DDGIHitShadePass();
        hitShade.RayBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayBufferName);
        hitShade.HitBufferName.ShouldBe(DefaultRenderPipeline.DDGIHitBufferName);
        hitShade.RayRadianceBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayRadianceBufferName);
        hitShade.ProbeBufferName.ShouldBe(DefaultRenderPipeline.DDGIProbeStateBufferName);
        hitShade.IrradianceAtlasTextureName.ShouldBe(DefaultRenderPipeline.DDGIIrradianceAtlasTextureName);
        hitShade.VisibilityAtlasTextureName.ShouldBe(DefaultRenderPipeline.DDGIVisibilityAtlasTextureName);

        var updateIrr = new XREngine.Rendering.Pipelines.Commands.VPRC_DDGIUpdateIrradiancePass();
        updateIrr.RayBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayBufferName);
        updateIrr.RayRadianceBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayRadianceBufferName);
        updateIrr.IrradianceAtlasTextureName.ShouldBe(DefaultRenderPipeline.DDGIIrradianceAtlasTextureName);

        var updateVis = new XREngine.Rendering.Pipelines.Commands.VPRC_DDGIUpdateVisibilityPass();
        updateVis.RayBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayBufferName);
        updateVis.RayRadianceBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayRadianceBufferName);
        updateVis.VisibilityAtlasTextureName.ShouldBe(DefaultRenderPipeline.DDGIVisibilityAtlasTextureName);

        var borderCopy = new XREngine.Rendering.Pipelines.Commands.VPRC_DDGIBorderCopyPass();
        borderCopy.IrradianceAtlasTextureName.ShouldBe(DefaultRenderPipeline.DDGIIrradianceAtlasTextureName);
        borderCopy.VisibilityAtlasTextureName.ShouldBe(DefaultRenderPipeline.DDGIVisibilityAtlasTextureName);
    }

    [Test]
    public void DDGISamplingSnippet_Phase4ViewBias_SupportsOverloads()
    {
        string snippet = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Snippets/DDGISampling.glsl");
        snippet.ShouldContain("vec3 sampleDDGI(vec3 worldPos, vec3 normal, vec3 viewDir)");
        snippet.ShouldContain("vec3 sampleDDGI(vec3 worldPos, vec3 normal)");
        snippet.ShouldContain("uViewBias");
        snippet.ShouldContain("biasedPos = worldPos + normal * normalBias + viewDir * viewBias;");
    }

    [Test]
    public void DeferredLightCombine_SpecularIblDecoupling_SuppressesClassicalDiffuseWhenDDGIActive()
    {
        string monoShader = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs");
        monoShader.ShouldContain("uniform bool UsesDDGI = false;");
        monoShader.ShouldContain("if (UsesDDGI)");
        monoShader.ShouldContain("probeAmbient = vec3(0.0f);");
        monoShader.ShouldContain("vec3 specular = prefilteredColor * (kS * brdfValue.x + brdfValue.y);");

        string stereoShader = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightCombineStereo.fs");
        stereoShader.ShouldContain("uniform bool UsesDDGI = false;");
        stereoShader.ShouldContain("if (UsesDDGI)");
        stereoShader.ShouldContain("probeAmbient = vec3(0.0f);");
        stereoShader.ShouldContain("vec3 specular = prefilteredColor * (kS * brdfValue.x + brdfValue.y);");
    }

    [Test]
    public void DDGIScreenSampleShaders_ExistAndDeclareCorrectLayout()
    {
        string mono = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_screen_sample.comp");
        mono.ShouldContain("#version 450");
        mono.ShouldContain("local_size_x = 16");
        mono.ShouldContain("local_size_y = 16");
        mono.ShouldContain("#pragma snippet \"DDGISampling\"");
        mono.ShouldContain("#pragma snippet \"NormalEncoding\"");
        mono.ShouldContain("uniform sampler2D gDepth;");
        mono.ShouldContain("uniform sampler2D gNormal;");
        mono.ShouldContain("uniform sampler2D gAlbedo;");
        mono.ShouldContain("uniform writeonly image2D uDDGITexture;");
        mono.ShouldContain("sampleDDGI(worldPos, normal, viewDir)");
        mono.ShouldContain("uDebugMode");

        string stereo = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_screen_sample_stereo.comp");
        stereo.ShouldContain("#version 450");
        stereo.ShouldContain("local_size_x = 16");
        stereo.ShouldContain("local_size_y = 16");
        stereo.ShouldContain("#pragma snippet \"DDGISampling\"");
        stereo.ShouldContain("uniform sampler2DArray gDepth;");
        stereo.ShouldContain("uniform sampler2DArray gNormal;");
        stereo.ShouldContain("uniform sampler2DArray gAlbedo;");
        stereo.ShouldContain("uniform writeonly image2DArray uDDGITexture;");
        stereo.ShouldContain("leftInvProjMatrix");
        stereo.ShouldContain("rightInvProjMatrix");
        stereo.ShouldContain("sampleDDGI(worldPos, normal, viewDir)");
    }

    [Test]
    public void DDGICompositeShaders_ExistAndDeclareCorrectLayout()
    {
        string mono = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Scene3D/DDGIComposite.fs");
        mono.ShouldContain("#version 450 core");
        mono.ShouldContain("#pragma snippet \"ScreenSpaceUtils\"");
        mono.ShouldContain("uniform sampler2D DDGITexture;");
        mono.ShouldContain("OutColor = vec4(gi.rgb, 0.0);");

        string stereo = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Scene3D/DDGICompositeStereo.fs");
        stereo.ShouldContain("#version 450 core");
        stereo.ShouldContain("#pragma snippet \"ScreenSpaceUtils\"");
        stereo.ShouldContain("uniform sampler2DArray DDGITexture;");
        stereo.ShouldContain("OutColor = vec4(gi.rgb, 0.0);");
    }

    [Test]
    public void VPRC_DDGICompositePass_Phase4_ConfiguredCorrectly()
    {
        var composite = new XREngine.Rendering.Pipelines.Commands.VPRC_DDGICompositePass();
        composite.DepthTextureName.ShouldBe(DefaultRenderPipeline.DepthViewTextureName);
        composite.NormalTextureName.ShouldBe(DefaultRenderPipeline.NormalTextureName);
        composite.AlbedoTextureName.ShouldBe(DefaultRenderPipeline.AlbedoOpacityTextureName);
        composite.OutputTextureName.ShouldBe(DefaultRenderPipeline.DDGITextureName);
        composite.CompositeQuadFBOName.ShouldBe(DefaultRenderPipeline.DDGICompositeFBOName);
        composite.ForwardFBOName.ShouldBe(DefaultRenderPipeline.ForwardPassFBOName);
        composite.IrradianceAtlasTextureName.ShouldBe(DefaultRenderPipeline.DDGIIrradianceAtlasTextureName);
        composite.VisibilityAtlasTextureName.ShouldBe(DefaultRenderPipeline.DDGIVisibilityAtlasTextureName);
        composite.ProbeStateBufferName.ShouldBe(DefaultRenderPipeline.DDGIProbeStateBufferName);
        composite.RayBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayBufferName);
        composite.HitBufferName.ShouldBe(DefaultRenderPipeline.DDGIHitBufferName);
        composite.DebugMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIDebugMode.None);
    }

    [Test]
    public void DDGIVolume_DebugModes_AreSyncedAndConfigured()
    {
        var volume = new DDGIVolumeComponent();
        volume.DebugMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIDebugMode.None);

        volume.DebugMode = XREngine.Rendering.GI.DDGI.EDDGIDebugMode.ProbeNeighborhood;
        volume.RuntimeState.Synchronize(volume);
        volume.RuntimeState.DebugMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIDebugMode.ProbeNeighborhood);

        volume.DebugMode = XREngine.Rendering.GI.DDGI.EDDGIDebugMode.DDGIOnly;
        volume.RuntimeState.Synchronize(volume);
        volume.RuntimeState.DebugMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIDebugMode.DDGIOnly);

        var viz = new XREngine.Rendering.Pipelines.Commands.VPRC_DDGIDebugVisualization();
        viz.DebugMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIDebugMode.None);
        viz.DebugMode = XREngine.Rendering.GI.DDGI.EDDGIDebugMode.DDGIOnly;
        viz.DebugMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIDebugMode.DDGIOnly);
    }

    [Test]
    public void VPRC_DDGIRelocatePass_Phase5_ConfiguredCorrectly()
    {
        var pass = new XREngine.Rendering.Pipelines.Commands.VPRC_DDGIRelocatePass();
        pass.ProbeBufferName.ShouldBe(DefaultRenderPipeline.DDGIProbeStateBufferName);
        pass.RayBufferName.ShouldBe(DefaultRenderPipeline.DDGIRayBufferName);
        pass.HitBufferName.ShouldBe(DefaultRenderPipeline.DDGIHitBufferName);
        pass.TriangleBufferVariableName.ShouldBe("AccelerationStructureTriangles");
    }

    [Test]
    public void DDGIRelocateShader_Phase5_ExistsAndDeclaresCorrectLayout()
    {
        string shader = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_relocate.comp");
        shader.ShouldContain("#version 450");
        shader.ShouldContain("local_size_x = 32");
        shader.ShouldContain("layout(std430, binding = 0) buffer ProbeBuffer");
        shader.ShouldContain("layout(std430, binding = 1) readonly buffer Rays");
        shader.ShouldContain("layout(std430, binding = 2) readonly buffer Hits");
        shader.ShouldContain("layout(std430, binding = 3) readonly buffer Triangles");
        shader.ShouldContain("uniform uint uProbeOffset;");
        shader.ShouldContain("uniform uint uScheduledProbeCount;");
        shader.ShouldContain("uniform uint uProbeCount;");
        shader.ShouldContain("uniform uint uRaysPerProbe;");
        shader.ShouldContain("uniform vec3 uProbeSpacing;");
        shader.ShouldContain("uniform float uMinDistance;");
        shader.ShouldContain("uniform float uRelocationStep;");
        shader.ShouldContain("uniform float uBackfaceThreshold;");
        shader.ShouldContain("uniform uint uRelocationEnabled;");
        shader.ShouldContain("uniform uint uClassificationEnabled;");
        shader.ShouldContain("clamp(finalOffset, -maxAllowedOffset, maxAllowedOffset)");
    }

    [Test]
    public void DDGISampling_Snippet_Phase5_ExcludesInactiveProbes()
    {
        string snippet = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Snippets/DDGISampling.glsl");
        snippet.ShouldContain("probeState > 0.5 && probeState < 1.5");
        snippet.ShouldContain("continue;");
    }

    [Test]
    public void DDGIVolume_Phase5Properties_AreSyncedAndConfigured()
    {
        var volume = new DDGIVolumeComponent();
        volume.FixedTimeBudgetMs.ShouldBe(0.0f);
        volume.RelocationMinDistance.ShouldBe(0.0f);
        volume.RelocationStepSize.ShouldBe(0.1f);
        volume.BackfaceHitRatioThreshold.ShouldBe(0.85f);

        volume.FixedTimeBudgetMs = 2.5f;
        volume.RelocationMinDistance = 0.35f;
        volume.RelocationStepSize = 0.2f;
        volume.BackfaceHitRatioThreshold = 0.75f;

        volume.RuntimeState.Synchronize(volume);
        volume.RuntimeState.FixedTimeBudgetMs.ShouldBe(2.5f);
        volume.RuntimeState.RelocationMinDistance.ShouldBe(0.35f);
        volume.RuntimeState.RelocationStepSize.ShouldBe(0.2f);
        volume.RuntimeState.BackfaceHitRatioThreshold.ShouldBe(0.75f);
    }

    [Test]
    public void DDGI_Relocation_DualGridConstraint_ClampsWithinHalfSpacing()
    {
        Vector3 spacing = new(2.0f, 3.0f, 4.0f);
        Vector3 maxAllowed = new(1.0f, 1.5f, 2.0f);

        // Within bounds
        Vector3 inside = new(0.5f, -1.0f, 1.8f);
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.IsWithinDualGridBounds(inside, spacing).ShouldBeTrue();
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ClampRelocationOffset(inside, spacing).ShouldBe(inside);

        // Outside bounds: positive exceedance
        Vector3 outsidePos = new(2.5f, 3.0f, 5.0f);
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.IsWithinDualGridBounds(outsidePos, spacing).ShouldBeFalse();
        Vector3 clampedPos = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ClampRelocationOffset(outsidePos, spacing);
        clampedPos.X.ShouldBe(maxAllowed.X);
        clampedPos.Y.ShouldBe(maxAllowed.Y);
        clampedPos.Z.ShouldBe(maxAllowed.Z);
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.IsWithinDualGridBounds(clampedPos, spacing).ShouldBeTrue();

        // Outside bounds: negative exceedance
        Vector3 outsideNeg = new(-4.0f, -2.0f, -3.5f);
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.IsWithinDualGridBounds(outsideNeg, spacing).ShouldBeFalse();
        Vector3 clampedNeg = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ClampRelocationOffset(outsideNeg, spacing);
        clampedNeg.X.ShouldBe(-maxAllowed.X);
        clampedNeg.Y.ShouldBe(-maxAllowed.Y);
        clampedNeg.Z.ShouldBe(-maxAllowed.Z);
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.IsWithinDualGridBounds(clampedNeg, spacing).ShouldBeTrue();
    }

    [Test]
    public void DDGI_Scheduler_RoundRobin_OffsetsAndWrapsCorrectly()
    {
        var volume = new DDGIVolumeComponent();
        volume.ProbeCounts = new IVector3(16, 8, 16); // 2048 probes
        volume.MaxProbesUpdatedPerFrame = 512;
        volume.RaysPerProbe = 64;

        var state = volume.RuntimeState;
        state.Synchronize(volume);

        state.TotalProbeCount.ShouldBe(2048);
        state.ScheduledProbeCount.ShouldBe(512);
        state.ProbeUpdateOffset.ShouldBe(0);
        state.ActiveRayBudget.ShouldBe(512 * 64);

        // Frame 0 completes: offset advances to 512
        state.OnFrameCompleted();
        state.ProbeUpdateOffset.ShouldBe(512);

        // Frame 1 completes: offset advances to 1024
        state.OnFrameCompleted();
        state.ProbeUpdateOffset.ShouldBe(1024);

        // Frame 2 completes: offset advances to 1536
        state.OnFrameCompleted();
        state.ProbeUpdateOffset.ShouldBe(1536);

        // Frame 3 completes: offset wraps back to 0
        state.OnFrameCompleted();
        state.ProbeUpdateOffset.ShouldBe(0);

        // Verify index calculation wraps properly
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeScheduledProbeIndex(10, 1536, 2048).ShouldBe(1546);
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeScheduledProbeIndex(600, 1536, 2048).ShouldBe(88); // (1536 + 600) % 2048 = 2136 % 2048 = 88
    }

    [Test]
    public void DDGI_Scheduler_FixedTimeMode_AdaptsToBudget()
    {
        var volume = new DDGIVolumeComponent();
        volume.ProbeCounts = new IVector3(16, 8, 16); // 2048 probes
        volume.MaxProbesUpdatedPerFrame = 256;
        volume.FixedTimeBudgetMs = 2.0f;
        volume.RaysPerProbe = 64;

        var state = volume.RuntimeState;
        state.Synchronize(volume);

        // Initial estimate from MaxProbesUpdatedPerFrame
        state.ScheduledProbeCount.ShouldBe(256);

        // Simulate 256 probes took 1.0 ms (0.00390625 ms per probe)
        state.MeasuredFrameTimeMs = 1.0f;
        // With 2.0 ms target, target probes = 2.0 / (1.0 / 256) = 512 probes
        int target = state.ResolveScheduledProbeCount();
        target.ShouldBe(512);
    }

    [Test]
    public void DefaultRenderPipeline_CommandChain_IncludesRelocatePass()
    {
        string commandChain = SourceContractWorkspace.ReadFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");
        commandChain.ShouldContain("c.Add<VPRC_DDGIHitShadePass>();");
        commandChain.ShouldContain("c.Add<VPRC_DDGIRelocatePass>();");
        commandChain.ShouldContain("c.Add<VPRC_DDGIUpdateIrradiancePass>();");
    }

    [Test]
    public void DDGI_GridSnapping_SnapsToNearestIntegerProbeInterval()
    {
        Vector3 spacing = new(1.0f, 2.0f, 4.0f);

        // Position exactly on grid
        Vector3 onGrid = new(5.0f, 6.0f, 8.0f);
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.SnapToGrid(onGrid, spacing).ShouldBe(onGrid);

        // Positive fractional offset (e.g. 5.7, 6.3, 9.8) -> Floor: 5.0, 6.0, 8.0
        Vector3 pos = new(5.7f, 6.3f, 9.8f);
        Vector3 snapped = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.SnapToGrid(pos, spacing);
        snapped.X.ShouldBe(5.0f, 0.0001f);
        snapped.Y.ShouldBe(6.0f, 0.0001f);
        snapped.Z.ShouldBe(8.0f, 0.0001f);

        // Negative coordinates: Floor(-1.2 / 1.0) * 1.0 = -2.0; Floor(-2.1 / 2.0) * 2.0 = -4.0
        Vector3 neg = new(-1.2f, -2.1f, -4.5f);
        Vector3 snappedNeg = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.SnapToGrid(neg, spacing);
        snappedNeg.X.ShouldBe(-2.0f, 0.0001f);
        snappedNeg.Y.ShouldBe(-4.0f, 0.0001f);
        snappedNeg.Z.ShouldBe(-8.0f, 0.0001f);
    }

    [Test]
    public void DDGI_CascadeHierarchy_SynchronizesBoundsIntervalsAndOffsets()
    {
        var volume = new DDGIVolumeComponent();
        volume.ProbeCounts = new IVector3(16, 8, 16);
        volume.HalfExtents = new Vector3(15f, 7f, 15f); // spacing = (2, 2, 2)
        volume.CascadeCount = 3;
        volume.CameraScrolling = true;
        volume.CascadeSpacingMultiplier = 2.0f;
        volume.DisableCoarseVisibility = true;
        volume.CascadeBlendMargin = 0.15f;

        var state = volume.RuntimeState;
        state.Synchronize(volume);

        state.CascadeCount.ShouldBe(3);
        state.Cascades.Count.ShouldBe(3);
        state.CascadeSpacingMultiplier.ShouldBe(2.0f);
        state.DisableCoarseVisibility.ShouldBeTrue();
        state.CascadeBlendMargin.ShouldBe(0.15f);

        // Cascade 0: fine (spacing 2, interval 1, visibility true, offset 0)
        var c0 = state.Cascades[0];
        c0.CascadeIndex.ShouldBe(0);
        c0.ProbeSpacing.X.ShouldBe(2.0f, 0.0001f);
        c0.HalfExtents.ShouldBe(new Vector3(15f, 7f, 15f));
        c0.UpdateInterval.ShouldBe(1);
        c0.VisibilityEnabled.ShouldBeTrue();
        c0.ProbeOffset.ShouldBe(0);
        c0.TotalProbeCount.ShouldBe(16 * 8 * 16);

        // Cascade 1: medium (spacing 4, interval 2, visibility true, offset 2048)
        var c1 = state.Cascades[1];
        c1.CascadeIndex.ShouldBe(1);
        c1.ProbeSpacing.X.ShouldBe(4.0f, 0.0001f);
        c1.HalfExtents.ShouldBe(new Vector3(30f, 14f, 30f));
        c1.UpdateInterval.ShouldBe(2);
        c1.VisibilityEnabled.ShouldBeTrue();
        c1.ProbeOffset.ShouldBe(2048);

        // Cascade 2: coarse (spacing 8, interval 4, visibility false (omitted!), offset 4096)
        var c2 = state.Cascades[2];
        c2.CascadeIndex.ShouldBe(2);
        c2.ProbeSpacing.X.ShouldBe(8.0f, 0.0001f);
        c2.HalfExtents.ShouldBe(new Vector3(60f, 28f, 60f));
        c2.UpdateInterval.ShouldBe(4);
        c2.VisibilityEnabled.ShouldBeFalse();
        c2.ProbeOffset.ShouldBe(4096);
    }

    [Test]
    public void DDGI_CascadeMemory_VerifiesStrictPerCascadeAndTotalBudgets()
    {
        // 32x4x32 grid = 4096 probes per cascade
        int px = 32, py = 4, pz = 32;

        // Cascade 0 & 1 (visibility enabled):
        // Irradiance: 32*6 = 192; 4*32*6 = 768; 192*768*4 = 589,824 bytes (~0.56 MB)
        // Visibility: 32*16 = 512; 4*32*16 = 2048; 512*2048*4 = 4,194,304 bytes (~4.00 MB)
        // Probe buffer: 4096 * 32 = 131,072 bytes (~0.125 MB)
        // Total per fine cascade: 4,915,200 bytes (~4.688 MB) < 5.0 MB!
        long fineMemory = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeCascadeMemoryBytes(px, py, pz, visibilityEnabled: true);
        fineMemory.ShouldBeLessThan(5L * 1024L * 1024L);
        fineMemory.ShouldBe(4_915_200L);

        // Cascade 2 (coarse, visibility omitted):
        // Irradiance + Probe buffer = 589,824 + 131,072 = 720,896 bytes (~0.687 MB) < 1.0 MB!
        long coarseMemory = XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.ComputeCascadeMemoryBytes(px, py, pz, visibilityEnabled: false);
        coarseMemory.ShouldBeLessThan(1L * 1024L * 1024L);
        coarseMemory.ShouldBe(720_896L);

        // Total 3-cascade memory budget: 2 * 4,915,200 + 720,896 = 10,551,296 bytes (~10.06 MB) << 20 MB!
        long totalMemory = fineMemory * 2 + coarseMemory;
        totalMemory.ShouldBeLessThan(20L * 1024L * 1024L);
    }

    [Test]
    public void DDGI_CascadeBoundaryBlending_SmoothstepTransitionIsAccurate()
    {
        float margin = 0.1f; // Blend begins at 1.0 - 0.1 = 0.9

        // Inside core: maxDist <= 0.9 -> factor = 0.0
        Vector3 deepInside = new(0.5f, 0.2f, 0.8f); // maxDist = 0.8 <= 0.9
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.CalculateCascadeBlendFactor(deepInside, margin).ShouldBe(0.0f);

        Vector3 edgeInside = new(0.9f, 0.0f, 0.0f);
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.CalculateCascadeBlendFactor(edgeInside, margin).ShouldBe(0.0f, 0.0001f);

        // Exactly halfway through blend band: maxDist = 0.95 -> t = 0.5
        // smoothstep(0.5) = 0.5 * 0.5 * (3 - 2 * 0.5) = 0.25 * 2.0 = 0.5
        Vector3 midBlend = new(0.95f, 0.0f, 0.0f);
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.CalculateCascadeBlendFactor(midBlend, margin).ShouldBe(0.5f, 0.0001f);

        // At or beyond boundary: maxDist >= 1.0 -> factor = 1.0
        Vector3 atBoundary = new(1.0f, 0.0f, 0.0f);
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.CalculateCascadeBlendFactor(atBoundary, margin).ShouldBe(1.0f, 0.0001f);

        Vector3 outside = new(1.2f, 0.0f, 0.0f);
        XREngine.Rendering.GI.DDGI.DDGIVolumeRuntimeState.CalculateCascadeBlendFactor(outside, margin).ShouldBe(1.0f);
    }

    [Test]
    public void DDGI_CascadeScheduler_DistributesUpdateCadenceAcrossFrames()
    {
        var volume = new DDGIVolumeComponent();
        volume.CascadeCount = 3;
        var state = volume.RuntimeState;
        state.SynchronizeCascades(volume);

        // Intervals: Cascade 0 = 1, Cascade 1 = 2, Cascade 2 = 4
        // Frame 0: 0 % 4 == 0 -> Cascade 2 (coarse)
        state.ResolveActiveCascadeIndex(0).ShouldBe(2);

        // Frame 1: 1 % 2 != 0, 1 % 4 != 0 -> Cascade 0 (fine)
        state.ResolveActiveCascadeIndex(1).ShouldBe(0);

        // Frame 2: 2 % 2 == 0, 2 % 4 != 0 -> Cascade 1 (medium)
        state.ResolveActiveCascadeIndex(2).ShouldBe(1);

        // Frame 3: 3 % 2 != 0, 3 % 4 != 0 -> Cascade 0 (fine)
        state.ResolveActiveCascadeIndex(3).ShouldBe(0);

        // Frame 4: 4 % 4 == 0 -> Cascade 2 (coarse)
        state.ResolveActiveCascadeIndex(4).ShouldBe(2);

        // Frame 5: -> Cascade 0 (fine)
        state.ResolveActiveCascadeIndex(5).ShouldBe(0);

        // Frame 6: -> Cascade 1 (medium)
        state.ResolveActiveCascadeIndex(6).ShouldBe(1);

        // Frame 7: -> Cascade 0 (fine)
        state.ResolveActiveCascadeIndex(7).ShouldBe(0);

        // Cascade 0 gets 50% of frames (1, 3, 5, 7), Cascade 1 gets 25% (2, 6), Cascade 2 gets 25% (0, 4)
    }

    [Test]
    public void DDGI_DebugMode_CascadeCoverage_IsConfigured()
    {
        XREngine.Rendering.GI.DDGI.EDDGIDebugMode.CascadeCoverage.ShouldBe((XREngine.Rendering.GI.DDGI.EDDGIDebugMode)3);

        var volume = new DDGIVolumeComponent();
        volume.DebugMode = XREngine.Rendering.GI.DDGI.EDDGIDebugMode.CascadeCoverage;
        volume.RuntimeState.Synchronize(volume);
        volume.RuntimeState.DebugMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIDebugMode.CascadeCoverage);
    }

    [Test]
    public void DDGIPhase6_ShadersAndPipelines_ContainCascadeContracts()
    {
        string sampling = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Snippets/DDGISampling.glsl");
        sampling.ShouldContain("uniform sampler2DArray uDDGIIrradianceAtlas;");
        sampling.ShouldContain("uniform sampler2DArray uDDGIVisibilityAtlas;");
        sampling.ShouldContain("uniform int uCascadeCount;");
        sampling.ShouldContain("uniform float uCascadeBlendMargin;");
        sampling.ShouldContain("uniform int uDisableVisibilityOnCoarse;");
        sampling.ShouldContain("uniform vec3 uCascadeGridMin[4];");
        sampling.ShouldContain("uniform vec3 uCascadeProbeSpacing[4];");
        sampling.ShouldContain("sampleDDGICascade");
        sampling.ShouldContain("smoothstep");

        string screenSampleMono = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_screen_sample.comp");
        screenSampleMono.ShouldContain("uDebugMode == 3");

        string screenSampleStereo = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_screen_sample_stereo.comp");
        screenSampleStereo.ShouldContain("uDebugMode == 3");

        string updateIrr = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_update_irradiance.comp");
        updateIrr.ShouldContain("layout(r11f_g11f_b10f, binding = 0) uniform image2DArray uIrradianceAtlas;");
        updateIrr.ShouldContain("uniform int uCascadeIndex;");

        string updateVis = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_update_visibility.comp");
        updateVis.ShouldContain("layout(rg16f, binding = 0) uniform image2DArray uVisibilityAtlas;");
        updateVis.ShouldContain("uniform int uCascadeIndex;");

        string borderIrr = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_border_copy.comp");
        borderIrr.ShouldContain("layout(r11f_g11f_b10f, binding = 0) uniform image2DArray uIrradianceAtlas;");
        borderIrr.ShouldContain("uniform int uCascadeIndex;");

        string borderVis = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_border_copy_visibility.comp");
        borderVis.ShouldContain("layout(rg16f, binding = 0) uniform image2DArray uVisibilityAtlas;");
        borderVis.ShouldContain("uniform int uCascadeIndex;");

        string resources = SourceContractWorkspace.ReadFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Resources.cs");
        resources.ShouldContain(".Layers(DDGIVolumeRuntimeState.DefaultMaxCascades)");
    }

    [Test]
    public void DDGIBakedAsset_Serialization_RoundTripsHeaderAndPayloadAccurately()
    {
        var asset = new XREngine.Rendering.GI.DDGI.DDGIBakedAsset
        {
            VolumeName = "TestCourtyardVolume",
            ProbeCounts = new IVector3(8, 4, 8),
            HalfExtents = new Vector3(12.0f, 6.0f, 12.0f),
            Origin = new Vector3(1.5f, 2.0f, -3.5f),
            CascadeCount = 2,
            CascadeSpacingMultiplier = 2.0f,
            IrradianceAtlasWidth = 48,
            IrradianceAtlasHeight = 192,
            VisibilityAtlasWidth = 128,
            VisibilityAtlasHeight = 512,
            NormalBias = 0.12f,
            ViewBias = 0.22f,
            ChebyshevPower = 5.0f,
            Intensity = 1.25f,
            Probes = new XREngine.Rendering.GI.DDGI.DDGIProbeGPU[8 * 4 * 8],
            IrradianceAtlasLayers = new byte[][]
            {
                new byte[48 * 192 * 4],
                new byte[48 * 192 * 4],
            },
            VisibilityAtlasLayers = new byte[][]
            {
                new byte[128 * 512 * 4],
                Array.Empty<byte>(), // coarse cascade visibility omitted
            }
        };

        // Populate sample probe positions and layer bytes with recognizable test patterns
        for (int i = 0; i < asset.Probes.Length; i++)
        {
            asset.Probes[i] = new XREngine.Rendering.GI.DDGI.DDGIProbeGPU
            {
                Position = new Vector4(i * 1.5f, i * 2.0f, i * 0.5f, (i % 7 == 0) ? 1.0f : 0.0f),
                RelocationOffset = new Vector4(0.1f, -0.05f, 0.08f, 0.95f),
            };
        }
        for (int i = 0; i < asset.IrradianceAtlasLayers[0].Length; i++)
        {
            asset.IrradianceAtlasLayers[0][i] = (byte)(i % 251);
        }
        for (int i = 0; i < asset.VisibilityAtlasLayers[0].Length; i++)
        {
            asset.VisibilityAtlasLayers[0][i] = (byte)(i % 241);
        }

        using var ms = new System.IO.MemoryStream();
        asset.Save(ms);
        ms.Position = 0;

        var loaded = XREngine.Rendering.GI.DDGI.DDGIBakedAsset.Load(ms);

        loaded.VolumeName.ShouldBe("TestCourtyardVolume");
        loaded.ProbeCounts.ShouldBe(new IVector3(8, 4, 8));
        loaded.HalfExtents.ShouldBe(new Vector3(12.0f, 6.0f, 12.0f));
        loaded.Origin.ShouldBe(new Vector3(1.5f, 2.0f, -3.5f));
        loaded.CascadeCount.ShouldBe(2);
        loaded.CascadeSpacingMultiplier.ShouldBe(2.0f);
        loaded.IrradianceAtlasWidth.ShouldBe(48);
        loaded.IrradianceAtlasHeight.ShouldBe(192);
        loaded.VisibilityAtlasWidth.ShouldBe(128);
        loaded.VisibilityAtlasHeight.ShouldBe(512);
        loaded.NormalBias.ShouldBe(0.12f, 0.0001f);
        loaded.ViewBias.ShouldBe(0.22f, 0.0001f);
        loaded.ChebyshevPower.ShouldBe(5.0f, 0.0001f);
        loaded.Intensity.ShouldBe(1.25f, 0.0001f);

        loaded.Probes.Length.ShouldBe(asset.Probes.Length);
        for (int i = 0; i < asset.Probes.Length; i++)
        {
            loaded.Probes[i].Position.ShouldBe(asset.Probes[i].Position);
            loaded.Probes[i].RelocationOffset.ShouldBe(asset.Probes[i].RelocationOffset);
        }

        loaded.IrradianceAtlasLayers.Length.ShouldBe(2);
        loaded.IrradianceAtlasLayers[0].SequenceEqual(asset.IrradianceAtlasLayers[0]).ShouldBeTrue();
        loaded.VisibilityAtlasLayers.Length.ShouldBe(2);
        loaded.VisibilityAtlasLayers[0].SequenceEqual(asset.VisibilityAtlasLayers[0]).ShouldBeTrue();
        loaded.VisibilityAtlasLayers[1].Length.ShouldBe(0);
    }

    [Test]
    public void DDGIBakedAsset_ApplyToVolume_ConfiguresVolumeAndState()
    {
        var asset = new XREngine.Rendering.GI.DDGI.DDGIBakedAsset
        {
            VolumeName = "Courtyard",
            ProbeCounts = new IVector3(12, 6, 12),
            HalfExtents = new Vector3(18.0f, 9.0f, 18.0f),
            CascadeCount = 2,
            CascadeSpacingMultiplier = 2.0f,
            NormalBias = 0.15f,
            ViewBias = 0.25f,
            ChebyshevPower = 6.0f,
            Intensity = 1.4f,
        };

        var volume = new DDGIVolumeComponent();
        asset.ApplyTo(volume);

        volume.ProbeCounts.ShouldBe(new IVector3(12, 6, 12));
        volume.HalfExtents.ShouldBe(new Vector3(18.0f, 9.0f, 18.0f));
        volume.CascadeCount.ShouldBe(2);
        volume.NormalBias.ShouldBe(0.15f, 0.0001f);
        volume.ViewBias.ShouldBe(0.25f, 0.0001f);
        volume.ChebyshevPower.ShouldBe(6.0f, 0.0001f);
        volume.Intensity.ShouldBe(1.4f, 0.0001f);
        volume.UpdateMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIUpdateMode.Baked);
        volume.BakedMode.ShouldBeTrue();
        volume.BakedAsset.ShouldBe(asset);

        volume.RuntimeState.UpdateMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIUpdateMode.Baked);
    }

    [Test]
    public void DDGIVolume_UpdateMode_ConfiguresAndSynchronizesCorrectly()
    {
        var volume = new DDGIVolumeComponent();
        volume.UpdateMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIUpdateMode.Dynamic);
        volume.BakedMode.ShouldBeFalse();
        volume.SlowUpdateIntervalFrames.ShouldBe(30);

        // Setting BakedMode updates UpdateMode
        volume.BakedMode = true;
        volume.UpdateMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIUpdateMode.Baked);

        // Setting UpdateMode updates BakedMode
        volume.UpdateMode = XREngine.Rendering.GI.DDGI.EDDGIUpdateMode.SlowUpdate;
        volume.BakedMode.ShouldBeFalse();

        volume.SlowUpdateIntervalFrames = 45;
        volume.RuntimeState.Synchronize(volume);

        volume.RuntimeState.UpdateMode.ShouldBe(XREngine.Rendering.GI.DDGI.EDDGIUpdateMode.SlowUpdate);
        volume.RuntimeState.SlowUpdateIntervalFrames.ShouldBe(45);
    }

    [Test]
    public void DDGI_ShouldRunUpdatePasses_RespectsOperationalModes()
    {
        var volume = new DDGIVolumeComponent();
        var state = volume.RuntimeState;

        // Dynamic mode: runs every frame
        volume.UpdateMode = XREngine.Rendering.GI.DDGI.EDDGIUpdateMode.Dynamic;
        state.Synchronize(volume);
        state.ShouldRunUpdatePasses(0).ShouldBeTrue();
        state.ShouldRunUpdatePasses(1).ShouldBeTrue();
        state.ShouldRunUpdatePasses(42).ShouldBeTrue();

        // Baked mode: never runs update passes (0 rays traced)
        volume.UpdateMode = XREngine.Rendering.GI.DDGI.EDDGIUpdateMode.Baked;
        state.Synchronize(volume);
        state.ShouldRunUpdatePasses(0).ShouldBeFalse();
        state.ShouldRunUpdatePasses(1).ShouldBeFalse();
        state.ShouldRunUpdatePasses(30).ShouldBeFalse();

        // SlowUpdate mode: periodic or when invalidated
        volume.UpdateMode = XREngine.Rendering.GI.DDGI.EDDGIUpdateMode.SlowUpdate;
        volume.SlowUpdateIntervalFrames = 10;
        state.Synchronize(volume);

        // Frame 0: 0 % 10 == 0 -> true
        state.ShouldRunUpdatePasses(0).ShouldBeTrue();

        // Frame 1 with IsInvalidated = true -> true
        state.Invalidate();
        state.ShouldRunUpdatePasses(1).ShouldBeTrue();

        // Clear invalidation (frame completed)
        state.OnFrameCompleted(); // now IsInvalidated = false
        state.ShouldRunUpdatePasses(1).ShouldBeFalse();
        state.ShouldRunUpdatePasses(5).ShouldBeFalse();
        state.ShouldRunUpdatePasses(9).ShouldBeFalse();
        state.ShouldRunUpdatePasses(10).ShouldBeTrue();
        state.ShouldRunUpdatePasses(20).ShouldBeTrue();
    }

    [Test]
    public void DDGIBakedAsset_InvalidMagicAndVersion_ThrowsExpectedExceptions()
    {
        // Corrupt magic
        using var msBadMagic = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(msBadMagic, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x12345678u); // Invalid magic
            writer.Write(1u);
        }
        msBadMagic.Position = 0;
        Should.Throw<System.IO.InvalidDataException>(() => XREngine.Rendering.GI.DDGI.DDGIBakedAsset.Load(msBadMagic));

        // Unsupported version
        using var msBadVersion = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(msBadVersion, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(XREngine.Rendering.GI.DDGI.DDGIBakedAsset.AssetMagic);
            writer.Write(999u); // Unsupported version
        }
        msBadVersion.Position = 0;
        Should.Throw<System.IO.InvalidDataException>(() => XREngine.Rendering.GI.DDGI.DDGIBakedAsset.Load(msBadVersion));
    }

    [Test]
    public void DDGIVolumeComponent_ApplyAmbientOcclusion_ConfiguresAndSynchronizesCorrectly()
    {
        var volume = new DDGIVolumeComponent();
        var state = volume.RuntimeState;

        volume.ApplyAmbientOcclusion.ShouldBeTrue();
        state.ApplyAmbientOcclusion.ShouldBeTrue();

        volume.ApplyAmbientOcclusion = false;
        state.ApplyAmbientOcclusion.ShouldBeFalse();
    }

    [Test]
    public void DDGIScreenSampleShaders_Phase8_DeclareAmbientOcclusionContracts()
    {
        string monoSource = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_screen_sample.comp");
        monoSource.ShouldContain("uAOTexture");
        monoSource.ShouldContain("uniform bool uUseAO");
        monoSource.ShouldContain("diffuseGI = irradiance * albedo.rgb * ao;");

        string stereoSource = SourceContractWorkspace.ReadFile("Build/CommonAssets/Shaders/Compute/DDGI/ddgi_screen_sample_stereo.comp");
        stereoSource.ShouldContain("uAOTexture");
        stereoSource.ShouldContain("uniform bool uUseAO");
        stereoSource.ShouldContain("diffuseGI = irradiance * albedo.rgb * ao;");
    }

    [Test]
    public void VPRC_DDGICompositePass_Phase8_DeclaresAmbientOcclusionInRenderGraph()
    {
        var pass = new XREngine.Rendering.Pipelines.Commands.VPRC_DDGICompositePass();
        RenderPassMetadataCollection metadata = new();
        RenderGraphDescribeContext context = new(metadata);

        pass.DescribeRenderPass(context);

        var passes = metadata.Build();
        passes.ShouldContain(p => p.Name == nameof(XREngine.Rendering.Pipelines.Commands.VPRC_DDGICompositePass));
        var compositePass = passes.Single(p => p.Name == nameof(XREngine.Rendering.Pipelines.Commands.VPRC_DDGICompositePass));
        compositePass.ResourceUsages.ShouldContain(u => u.ResourceName == $"tex::{DefaultRenderPipeline.AmbientOcclusionIntensityTextureName}");
    }

    [Test]
    public void UnitTestingWorld_DDGI_Configuration_IsAvailable()
    {
        var settings = new XREngine.Runtime.Bootstrap.UnitTestingWorldSettings();
        settings.GlobalIlluminationMode = EGlobalIlluminationMode.DDGI;
        settings.InitializeDDGIVolume.ShouldBeFalse(); // Defaults to false, but mode is DDGI

        var rootNode = new SceneNode("TestRoot");
        XREngine.Runtime.Bootstrap.RuntimeBootstrapState.Settings = settings;

        var volume = XREngine.Runtime.Bootstrap.Builders.BootstrapLightingBuilder.AddConfiguredDDGIVolume(rootNode);
        volume.ShouldNotBeNull();
        volume.Name.ShouldBe("DDGIVolume");
        volume.ProbeCounts.X.ShouldBe(settings.DDGIVolumeProbeCounts.X);
        volume.ProbeCounts.Y.ShouldBe(settings.DDGIVolumeProbeCounts.Y);
        volume.ProbeCounts.Z.ShouldBe(settings.DDGIVolumeProbeCounts.Z);
    }
}

