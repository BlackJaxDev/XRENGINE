using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedPreparationIntegrationContractTests
{
    [Test]
    public void DesktopAndEyeConsumersAcquireOneSharedWorldPreparation()
    {
        using AdvancedSharedPreparationService service = new(
            CreateSmallOptions());
        GPUScene scene = new();
        AdvancedPreparationSceneContextProbe context = new();
        RenderWorldSnapshot world = new(
            FrameId: 12UL,
            Scene: context,
            GpuScene: scene);
        RenderFrameViewSet desktop = CreateViewSet(
            100UL,
            EVrOutputViewKind.DesktopEditor,
            width: 1920u,
            height: 1080u);
        RenderFrameViewSet eyes = CreateStereoViewSet();

        AdvancedPreparationPublication desktopPublication =
            service.Acquire(
                world,
                desktop,
                EAdvancedPreparationConsumer.Visibility |
                EAdvancedPreparationConsumer.MaterialReconstruction);
        AdvancedPreparationPublication eyePublication =
            service.Acquire(
                world,
                eyes,
                EAdvancedPreparationConsumer.Visibility |
                EAdvancedPreparationConsumer.Velocity);

        eyePublication.PublicationGeneration.ShouldBe(
            desktopPublication.PublicationGeneration);
        eyePublication.SceneIdentity.ShouldBe(
            desktopPublication.SceneIdentity);
        eyePublication.DeformationJobCount.ShouldBe(
            desktopPublication.DeformationJobCount);
        eyePublication.VisibilityViewCount.ShouldBe(3u);
        (eyePublication.Consumers &
            EAdvancedPreparationConsumer.MaterialReconstruction)
            .ShouldBe(EAdvancedPreparationConsumer.MaterialReconstruction);
        (eyePublication.Consumers &
            EAdvancedPreparationConsumer.Velocity)
            .ShouldBe(EAdvancedPreparationConsumer.Velocity);
        eyePublication.RequiresCpuReadback.ShouldBeFalse();
        eyePublication.WarmedManagedAllocationFree.ShouldBeTrue();
    }

    [Test]
    public void RvcStartsWithSharedPreparationAndAdvancedOwnsFrameBegin()
    {
        RvcRenderPipeline rvc = new(stereo: true);
        rvc.CommandChain.Count.ShouldBeGreaterThan(0);
        rvc.CommandChain[0]
            .ShouldBeOfType<VPRC_AcquireAdvancedPreparation>();

        AdvancedRenderPipeline desktop = new();
        desktop.CommandChain.Commands
            .OfType<VPRC_AdvancedRenderStage>()
            .First()
            .Stage.ShouldBe(EAdvancedRenderStage.FrameBegin);
    }

    [Test]
    public void OpenGlAndVulkanUseOneLogicalShaderAndExplicitBarriers()
    {
        AdvancedDeformationBackendContract
            .SupportsProductionAggregateCompute(
                RuntimeGraphicsApiKind.OpenGL)
            .ShouldBeTrue();
        AdvancedDeformationBackendContract
            .SupportsProductionAggregateCompute(
                RuntimeGraphicsApiKind.Vulkan)
            .ShouldBeTrue();
        AdvancedDeformationBackendContract.AggregateShaderPath
            .ShouldBe("Advanced/Preparation/AggregateDeformation.comp");
        AdvancedDeformationBackendContract.ResolveSynchronizationMode(
                RuntimeGraphicsApiKind.OpenGL,
                vulkanSynchronization2: false)
            .ShouldBe(
                EAdvancedSynchronizationMode.OpenGlMemoryBarrier);
        AdvancedDeformationBackendContract.ResolveSynchronizationMode(
                RuntimeGraphicsApiKind.Vulkan,
                vulkanSynchronization2: true)
            .ShouldBe(
                EAdvancedSynchronizationMode.VulkanSynchronization2);

        AdvancedPreparationBarrier visibility =
            AdvancedDeformationBarrierContract.Get(
                EAdvancedPreparationConsumer.Visibility);
        (visibility.OpenGlMask & EAdvancedOpenGlMemoryBarrier.Command)
            .ShouldBe(EAdvancedOpenGlMemoryBarrier.Command);
        (visibility.OpenGlMask &
            EAdvancedOpenGlMemoryBarrier.VertexAttributeArray)
            .ShouldBe(EAdvancedOpenGlMemoryBarrier.VertexAttributeArray);
        AdvancedPreparationBarrier reconstruction =
            AdvancedDeformationBarrierContract.Get(
                EAdvancedPreparationConsumer.MaterialReconstruction);
        reconstruction.OpenGlMask.ShouldBe(
            EAdvancedOpenGlMemoryBarrier.ShaderStorage);
    }

    [Test]
    public void ShaderSourcesEncodeAggregateOrderAndGpuOnlyVisibilityCounts()
    {
        string deformation = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Advanced/Preparation/AggregateDeformation.comp");
        string early = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Advanced/Preparation/EarlyVisibility.comp");
        string late = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Advanced/Preparation/LateVisibility.comp");
        string depthPyramid = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Advanced/Preparation/BuildDepthPyramid.comp");
        string indirect = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Advanced/Preparation/BuildVisibilityIndirect.comp");

        deformation.ShouldContain("applySparseBlendshapes(job");
        deformation.IndexOf(
                "applySparseBlendshapes(job",
                StringComparison.Ordinal)
            .ShouldBeLessThan(deformation.IndexOf(
                "if ((job.Features & FEATURE_SKINNING)",
                StringComparison.Ordinal));
        deformation.ShouldContain("FEATURE_PRECOMPOSED_PALETTE");
        deformation.ShouldContain(
            "layout(std430, binding = 11) readonly buffer BlendshapeRanges");
        deformation.ShouldContain(
            "layout(std430, binding = 12) readonly buffer BlendshapeRecords");
        deformation.ShouldContain("GroupedJobIndices");
        deformation.ShouldContain("CurrentOutput[job.CurrentVertexOffset");
        early.ShouldContain("atomicAdd(EarlyCount");
        early.ShouldContain("atomicAdd(DeferredCount");
        early.ShouldContain("EarlyVisibleIndices[outputIndex]");
        early.ShouldContain("PreviousViewProjection");
        early.ShouldContain("candidate.ViewMask & ActiveViewMask");
        early.ShouldContain("uniform uint ReversedDepth");
        late.ShouldContain("deferredIndex >= DeferredCount");
        late.ShouldContain("atomicAdd(LateCount");
        late.ShouldContain("LateVisibleIndices[outputIndex]");
        late.ShouldContain("uniform uint ReversedDepth");
        depthPyramid.ShouldContain("ReversedDepth != 0u");
        depthPyramid.ShouldContain("clamp(source + ivec2");
        indirect.ShouldContain("PRODUCER_SKINNED_MESHLET");
        indirect.ShouldContain("PRODUCER_STATIC_MESHLET");
        indirect.ShouldContain("visibleIndex >= visibleCount");
        indirect.ShouldContain("atomicAdd(Counts[range]");
    }

    [TestCase("AggregateDeformation.comp")]
    [TestCase("EarlyVisibility.comp")]
    [TestCase("LateVisibility.comp")]
    [TestCase("BuildDepthPyramid.comp")]
    [TestCase("BuildVisibilityIndirect.comp")]
    public void AdvancedPreparationShadersCompileToSpirvForVulkan(
        string shaderName)
    {
        string relativePath =
            $"Build/CommonAssets/Shaders/Advanced/Preparation/{shaderName}";
        string fullPath = ResolveWorkspaceFile(relativePath);
        TextFile source = new()
        {
            FilePath = fullPath,
            Text = File.ReadAllText(fullPath),
        };
        XRShader shader = new(EShaderType.Compute, source);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNullOrWhiteSpace();
        rewrittenSource.ShouldContain("XRENGINE_VULKAN");
    }

    private static AdvancedPreparationOptions CreateSmallOptions()
        => new(
            MaximumDraws: 16,
            MaximumDeformationJobs: 8,
            MaximumDeformationFamilies: 4,
            MaximumIndirectRanges: 8,
            MaximumViews: 4,
            DeformedArena: new AdvancedDeformedVertexArenaOptions(
                InitialVertexCapacity: 16u,
                FrameSlotCount: 3,
                OwnerCapacity: 8,
                RetiredGenerationCapacity: 2),
            DeformationBudget: new AdvancedDeformationBudget(
                MaximumJobs: 8u,
                MaximumVertices: 1_024UL,
                MaximumOutputBytes: 65_536UL,
                EAdvancedDeformationOverflowBehavior.KeepPreviousAndInvalidateVelocity),
            FrameUploadArena: new AdvancedFrameSlotUploadArenaOptions(
                SlotCount: 3u,
                InitialCapacity: SmallUploadCapacity(),
                OverflowCapacity: SmallUploadCapacity(),
                DefaultAlignmentBytes: 16u,
                MaxDirtyRangesPerStream: 2,
                OverflowGenerationCount: 1,
                RetiredGenerationCapacity: 1));

    private static AdvancedFrameUploadCapacityProfile SmallUploadCapacity()
        => new(
            InstanceBytes: 256u,
            ViewBytes: 256u,
            DeformationJobBytes: 2_048u,
            LightBytes: 256u,
            MaterialBytes: 256u);

    private static RenderFrameViewSet CreateViewSet(
        ulong historyKey,
        EVrOutputViewKind kind,
        uint width,
        uint height)
    {
        RenderFrameViewDescriptor[] views =
        [
            CreateView(
                viewId: 0u,
                historyKey,
                kind,
                openXrViewIndex:
                    kind == EVrOutputViewKind.DesktopEditor ? -1 : 0,
                outputLayer: 0u,
                width,
                height),
        ];
        return RenderFrameViewSet.Create(
            EVrViewRenderMode.SequentialViews,
            EVrVisibilityPolicy.SharedFrameViewSet,
            visibilityGroupCount: 1,
            views);
    }

    private static RenderFrameViewSet CreateStereoViewSet()
    {
        RenderFrameViewDescriptor[] views =
        [
            CreateView(
                0u,
                200UL,
                EVrOutputViewKind.LeftEye,
                0,
                0u,
                1440u,
                1600u),
            CreateView(
                1u,
                201UL,
                EVrOutputViewKind.RightEye,
                1,
                1u,
                1440u,
                1600u),
        ];
        return RenderFrameViewSet.Create(
            EVrViewRenderMode.SinglePassStereo,
            EVrVisibilityPolicy.SharedFrameViewSet,
            visibilityGroupCount: 1,
            views);
    }

    private static RenderFrameViewDescriptor CreateView(
        uint viewId,
        ulong historyKey,
        EVrOutputViewKind kind,
        int openXrViewIndex,
        uint outputLayer,
        uint width,
        uint height)
        => new(
            viewId,
            kind,
            RenderFrameViewDescriptor.InvalidViewId,
            VisibilityGroupIndex: 0,
            openXrViewIndex,
            outputLayer,
            RenderFrameViewRect.FromSize(width, height),
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            ViewFoveationContext.Off(),
            DebugName: kind.ToString(),
            HistoryKey: historyKey);

    private static string ReadWorkspaceFile(string relativePath)
        => File.ReadAllText(ResolveWorkspaceFile(relativePath));

    private static string ResolveWorkspaceFile(string relativePath)
    {
        DirectoryInfo? directory =
            new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string fullPath = Path.Combine(
                directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
                return fullPath;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not resolve '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }
}
