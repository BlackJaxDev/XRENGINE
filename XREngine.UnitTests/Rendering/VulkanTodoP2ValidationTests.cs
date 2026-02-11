using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.UI;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanTodoP2ValidationTests
{
    [Test]
    public void CpuOctreeVisibility_CollectsOnlyIntersectingRenderables()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueForward;
        var scene = new VisualScene3D();
        scene.SetBounds(AABB.FromCenterSize(Vector3.Zero, new Vector3(256.0f, 256.0f, 256.0f)));
        scene.ApplyRenderDispatchPreference(false);

        int renderedCount = 0;
        RenderInfo3D visible = CreateRenderInfo3D(pass, new Vector3(0.0f, 0.0f, -5.0f), () => renderedCount++);
        RenderInfo3D hidden = CreateRenderInfo3D(pass, new Vector3(0.0f, 0.0f, 200.0f), () => renderedCount++);

        scene.AddRenderable(visible);
        scene.AddRenderable(hidden);
        scene.GlobalCollectVisible();
        scene.GlobalSwapBuffers();

        RenderCommandCollection commands = CreateCommandCollection(pass);
        var cullingFrustum = new Frustum(
            width: 40.0f,
            height: 40.0f,
            nearPlane: 0.1f,
            farPlane: 50.0f);
        scene.CollectRenderedItems(commands, cullingFrustum, null, false);

        commands.SwapBuffers();
        commands.RenderCPU(pass, false);

        renderedCount.ShouldBe(1);
    }

    [Test]
    public void ScreenSpaceUiVisibility_CollectsOnlyVisibleBounds()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        var scene = new VisualScene2D();
        scene.SetBounds(new BoundingRectangleF(-100.0f, -100.0f, 200.0f, 200.0f));

        int renderedCount = 0;
        RenderInfo2D inside = CreateRenderInfo2D(pass, new BoundingRectangleF(0.0f, 0.0f, 16.0f, 16.0f), () => renderedCount++);
        RenderInfo2D outside = CreateRenderInfo2D(pass, new BoundingRectangleF(80.0f, 80.0f, 16.0f, 16.0f), () => renderedCount++);

        scene.AddRenderable(inside);
        scene.AddRenderable(outside);
        scene.GlobalCollectVisible();
        scene.GlobalSwapBuffers();

        RenderCommandCollection commands = CreateCommandCollection(pass);
        scene.CollectRenderedItems(commands, new BoundingRectangleF(-8.0f, -8.0f, 32.0f, 32.0f), null);

        commands.SwapBuffers();
        commands.RenderCPU(pass, false);

        renderedCount.ShouldBe(1);
    }

    [Test]
    public void UiTransparencyDefaults_UseExpectedBlendState()
    {
        var component = new UIMaterialComponent();
        BlendMode? blend = component.Material?.RenderOptions?.BlendModeAllDrawBuffers;

        blend.ShouldNotBeNull();
        blend.Enabled.ShouldBe(ERenderParamUsage.Enabled);
        blend.RgbEquation.ShouldBe(EBlendEquationMode.FuncAdd);
        blend.AlphaEquation.ShouldBe(EBlendEquationMode.FuncAdd);
        blend.RgbSrcFactor.ShouldBe(EBlendingFactor.SrcAlpha);
        blend.AlphaSrcFactor.ShouldBe(EBlendingFactor.SrcAlpha);
        blend.RgbDstFactor.ShouldBe(EBlendingFactor.OneMinusSrcAlpha);
        blend.AlphaDstFactor.ShouldBe(EBlendingFactor.OneMinusSrcAlpha);
        component.Material?.RenderOptions?.DepthTest.Enabled.ShouldBe(ERenderParamUsage.Disabled);
    }

    [Test]
    public void StrictOneByOneMode_RendersExpectedPerItemDrawCalls()
    {
        var canvas = new UICanvasComponent
        {
            StrictOneByOneRenderCalls = true
        };

        const int pass = (int)EDefaultRenderPass.TransparentForward;
        int drawCalls = 0;
        RenderCommandCollection commands = canvas.RenderPipelineInstance.MeshRenderCommands;
        bool previousTracking = Engine.Rendering.Stats.EnableTracking;
        try
        {
            Engine.Rendering.Stats.EnableTracking = true;
            Engine.Rendering.Stats.BeginFrame();

            commands.AddCPU(new RenderCommandMethod2D(pass, () =>
            {
                drawCalls++;
                Engine.Rendering.Stats.IncrementDrawCalls();
            }));
            commands.AddCPU(new RenderCommandMethod2D(pass, () =>
            {
                drawCalls++;
                Engine.Rendering.Stats.IncrementDrawCalls();
            }));
            commands.AddCPU(new RenderCommandMethod2D(pass, () =>
            {
                drawCalls++;
                Engine.Rendering.Stats.IncrementDrawCalls();
            }));
            commands.SwapBuffers();

            var renderUiBatched = new VPRC_RenderUIBatched
            {
                RenderPass = pass
            };

            using (Engine.Rendering.State.PushRenderingPipeline(canvas.RenderPipelineInstance))
            using (canvas.RenderPipelineInstance.RenderState.PushMainAttributes(
                       viewport: null,
                       scene: new VisualScene2D(),
                       camera: null,
                       stereoRightEyeCamera: null,
                       target: null,
                       shadowPass: false,
                       stereoPass: false,
                       globalMaterialOverride: null,
                       screenSpaceUI: null,
                       meshRenderCommands: commands))
            {
                renderUiBatched.ExecuteIfShould();
            }

            Engine.Rendering.Stats.BeginFrame();

            drawCalls.ShouldBe(3);
            Engine.Rendering.Stats.DrawCalls.ShouldBe(3);
            canvas.BatchCollector.Enabled.ShouldBeFalse();
            canvas.BatchCollector.HasBatchData.ShouldBeFalse();
        }
        finally
        {
            Engine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }

    [Test]
    public void BranchExecutedPassWithoutMetadata_EmitsCoverageWarning()
    {
        XREngine.Debug.ClearConsoleEntries(XREngine.ELogCategory.Rendering);

        var pipeline = new BranchMetadataGapPipeline();
        var instance = new XRRenderPipelineInstance(pipeline);
        instance.Render(
            scene: new VisualScene2D(),
            camera: null,
            stereoRightEyeCamera: null,
            viewport: null,
            targetFBO: null,
            userInterface: null,
            shadowPass: false,
            stereoPass: false);

        bool hasBranchCoverageWarning = XREngine.Debug.GetConsoleEntries().Any(entry =>
            entry.Category == XREngine.ELogCategory.Rendering &&
            entry.Message.Contains("branch-selected passes without metadata", StringComparison.OrdinalIgnoreCase));

        hasBranchCoverageWarning.ShouldBeTrue();
    }

    [Test]
    public void BarrierPlanner_TracksQueueOwnershipForBufferHazards()
    {
        var metadata = new RenderPassMetadataCollection();
        metadata.ForPass(100, "ComputeWrite", RenderGraphPassStage.Compute)
            .WriteBuffer("buf::SharedBuffer", RenderPassResourceType.StorageBuffer);
        metadata.ForPass(101, "GraphicsRead", RenderGraphPassStage.Graphics)
            .DependsOn(100)
            .ReadBuffer("buf::SharedBuffer", RenderPassResourceType.StorageBuffer);

        var planner = new VulkanBarrierPlanner();
        var resourcePlanner = new VulkanResourcePlanner();
        var allocator = new VulkanResourceAllocator();

        planner.Rebuild(
            metadata.Build(),
            resourcePlanner,
            allocator,
            synchronization: null,
            queueOwnership: new VulkanBarrierPlanner.QueueOwnershipConfig(
                GraphicsQueueFamilyIndex: 3u,
                ComputeQueueFamilyIndex: 7u,
                TransferQueueFamilyIndex: 11u));

        planner.BufferBarriers.ShouldContain(b =>
            b.PassIndex == 101 &&
            b.ResourceName.Equals("SharedBuffer", StringComparison.OrdinalIgnoreCase));

        var barrier = planner.BufferBarriers.Single(b =>
            b.PassIndex == 101 &&
            b.ResourceName.Equals("SharedBuffer", StringComparison.OrdinalIgnoreCase));

        barrier.SrcQueueFamilyIndex.ShouldBe(7u);
        barrier.DstQueueFamilyIndex.ShouldBe(3u);
    }

    private static RenderCommandCollection CreateCommandCollection(int passIndex)
        => new(new Dictionary<int, IComparer<RenderCommand>?>
        {
            { passIndex, null }
        });

    private static RenderInfo3D CreateRenderInfo3D(int renderPass, Vector3 worldCenter, Action onRender)
    {
        var owner = new TestRenderable();
        var command = new RenderCommandMethod3D(renderPass, () => onRender());
        RenderInfo3D info = RenderInfo3D.New(owner, command);
        owner.RenderedObjects = [info];
        info.LocalCullingVolume = AABB.FromCenterSize(Vector3.Zero, Vector3.One);
        info.CullingOffsetMatrix = Matrix4x4.CreateTranslation(worldCenter);
        return info;
    }

    private static RenderInfo2D CreateRenderInfo2D(int renderPass, BoundingRectangleF bounds, Action onRender)
    {
        var owner = new TestRenderable();
        var command = new RenderCommandMethod2D(renderPass, onRender);
        RenderInfo2D info = RenderInfo2D.New(owner, command);
        owner.RenderedObjects = [info];
        info.CullingVolume = bounds;
        return info;
    }

    private sealed class TestRenderable : IRenderable
    {
        public RenderInfo[] RenderedObjects { get; set; } = [];
    }

    private sealed class PushPassIndexOnlyCommand : ViewportRenderCommand
    {
        public int PassIndexToPush { get; set; } = int.MinValue;

        protected override void Execute()
        {
            using var passScope = Engine.Rendering.State.PushRenderGraphPassIndex(PassIndexToPush);
        }
    }

    private sealed class BranchMetadataGapPipeline : RenderPipeline
    {
        private const int MissingBranchPassIndex = 999;

        protected override Lazy<XRMaterial> InvalidMaterialFactory
            => new(XRMaterial.CreateUnlitColorMaterialForward, LazyThreadSafetyMode.PublicationOnly);

        protected override ViewportRenderCommandContainer GenerateCommandChain()
        {
            var root = new ViewportRenderCommandContainer(this);
            var ifElse = root.Add<VPRC_IfElse>();
            ifElse.ConditionEvaluator = static () => true;

            var trueBranch = new ViewportRenderCommandContainer(this);
            trueBranch.Add(new PushPassIndexOnlyCommand
            {
                PassIndexToPush = MissingBranchPassIndex
            });

            ifElse.TrueCommands = trueBranch;
            ifElse.FalseCommands = new ViewportRenderCommandContainer(this);
            return root;
        }

        protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
            => new()
            {
                { (int)EDefaultRenderPass.OpaqueForward, null }
            };
    }
}
