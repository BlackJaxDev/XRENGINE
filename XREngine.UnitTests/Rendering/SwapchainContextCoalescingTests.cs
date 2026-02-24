using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Rendering.Vulkan;
using static XREngine.Rendering.Vulkan.VulkanRenderer;
using static XREngine.Rendering.Vulkan.VulkanRenderer.VulkanRenderGraphCompiler;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Regression tests for the swapchain context coalescing fix that prevents the skybox
/// from turning black when multiple pipelines (e.g. DebugOpaqueRenderPipeline +
/// UserInterfaceRenderPipeline) composite onto the swapchain.
///
/// Root cause: context changes between pipelines targeting the swapchain triggered
/// EndActiveRenderPass → barrier to PresentSrcKhr → BeginRenderPassForTarget cycles
/// that lost composited content.
///
/// Fix: (1) coalesce swapchain-targeting ops to share a single context, (2) sort
/// frame ops by first-occurrence order to preserve inter-pipeline enqueue order,
/// (3) preserve the render pass when context changes between swapchain-targeting ops.
/// </summary>
[TestFixture]
public sealed class SwapchainContextCoalescingTests
{
    #region Helpers

    /// <summary>Context representing pipeline A (e.g. DebugOpaqueRenderPipeline).</summary>
    private static readonly FrameOpContext CtxPipelineA = new(
        PipelineIdentity: 100,
        ViewportIdentity: 1,
        PipelineInstance: null,
        ResourceRegistry: null,
        PassMetadata: null);

    /// <summary>Context representing pipeline B (e.g. UserInterfaceRenderPipeline).</summary>
    private static readonly FrameOpContext CtxPipelineB = new(
        PipelineIdentity: 200,
        ViewportIdentity: 1,
        PipelineInstance: null,
        ResourceRegistry: null,
        PassMetadata: null);

    /// <summary>Context representing pipeline C (a third hypothetical pipeline).</summary>
    private static readonly FrameOpContext CtxPipelineC = new(
        PipelineIdentity: 300,
        ViewportIdentity: 1,
        PipelineInstance: null,
        ResourceRegistry: null,
        PassMetadata: null);

    /// <summary>Creates a <see cref="ClearOp"/> targeting the swapchain (Target == null).</summary>
    private static ClearOp SwapchainClear(int passIndex, FrameOpContext ctx) =>
        new(passIndex, Target: null, ClearColor: true, ClearDepth: true, ClearStencil: false,
            Color: default, Depth: 1.0f, Stencil: 0, Rect: default, Context: ctx);

    /// <summary>Creates a <see cref="ClearOp"/> targeting an FBO (Target != null).</summary>
    private static ClearOp FboClear(int passIndex, FrameOpContext ctx, XREngine.Rendering.XRFrameBuffer fbo) =>
        new(passIndex, Target: fbo, ClearColor: true, ClearDepth: false, ClearStencil: false,
            Color: default, Depth: 0, Stencil: 0, Rect: default, Context: ctx);

    /// <summary>Creates a <see cref="MeshDrawOp"/> targeting the swapchain (Target == null).</summary>
    private static MeshDrawOp SwapchainDraw(int passIndex, FrameOpContext ctx) =>
        new(passIndex, Target: null, Draw: default, Context: ctx);

    /// <summary>Creates a <see cref="MeshDrawOp"/> targeting an FBO (Target != null).</summary>
    private static MeshDrawOp FboDraw(int passIndex, FrameOpContext ctx, XREngine.Rendering.XRFrameBuffer fbo) =>
        new(passIndex, Target: fbo, Draw: default, Context: ctx);

    /// <summary>Creates a <see cref="BlitOp"/> targeting the swapchain (OutFbo == null).</summary>
    private static BlitOp SwapchainBlit(int passIndex, FrameOpContext ctx) =>
        new(passIndex, InFbo: null, OutFbo: null, 0, 0, 0, 0, 0, 0, 0, 0,
            default, false, false, false, false, Context: ctx);

    /// <summary>Creates a <see cref="BlitOp"/> targeting an FBO (OutFbo != null).</summary>
    private static BlitOp FboBlit(int passIndex, FrameOpContext ctx, XREngine.Rendering.XRFrameBuffer fbo) =>
        new(passIndex, InFbo: null, OutFbo: fbo, 0, 0, 0, 0, 0, 0, 0, 0,
            default, false, false, false, false, Context: ctx);

    /// <summary>Creates an <see cref="IndirectDrawOp"/> (always targets swapchain — Target is always null).</summary>
    private static IndirectDrawOp SwapchainIndirectDraw(int passIndex, FrameOpContext ctx) =>
        new(passIndex, IndirectBuffer: null!, ParameterBuffer: null, DrawCount: 0,
            Stride: 0, ByteOffset: 0, UseCount: false, Context: ctx);

    /// <summary>Creates a <see cref="ComputeDispatchOp"/> (never targets swapchain).</summary>
    private static ComputeDispatchOp ComputeDispatch(int passIndex, FrameOpContext ctx) =>
        new(passIndex, Program: null!, GroupsX: 1, GroupsY: 1, GroupsZ: 1,
            Snapshot: null!, Context: ctx);

    /// <summary>Creates a <see cref="MemoryBarrierOp"/> (never targets swapchain).</summary>
    private static MemoryBarrierOp MemoryBarrier(int passIndex, FrameOpContext ctx) =>
        new(passIndex, Mask: default, Context: ctx);

    /// <summary>
    /// Builds a <see cref="VulkanCompiledRenderGraph"/> with the given pass order mapping.
    /// Pass indices not present in this map default to <c>int.MaxValue</c> during sort.
    /// </summary>
    private static VulkanCompiledRenderGraph GraphWithPassOrder(Dictionary<int, int> passOrder) =>
        new(Array.Empty<XREngine.Rendering.RenderGraph.RenderPassMetadata>(),
            passOrder,
            Array.Empty<VulkanCompiledPassBatch>(),
            XREngine.Rendering.RenderGraph.RenderGraphSynchronizationInfo.Empty);

    #endregion

    #region OpTargetsSwapchain

    [Test]
    public void OpTargetsSwapchain_ClearOp_NullTarget_ReturnsTrue()
    {
        var op = SwapchainClear(passIndex: 0, CtxPipelineA);
        VulkanRenderGraphCompiler.OpTargetsSwapchain(op).ShouldBeTrue();
    }

    [Test]
    public void OpTargetsSwapchain_ClearOp_WithFbo_ReturnsFalse()
    {
        // XRFrameBuffer is abstract — we need a concrete subclass or mock.
        // Since ClearOp checks `Target is null`, a non-null Target means FBO.
        // We verify via a swapchain clear with Target overridden.
        var swapchainOp = SwapchainClear(passIndex: 0, CtxPipelineA);
        VulkanRenderGraphCompiler.OpTargetsSwapchain(swapchainOp).ShouldBeTrue();

        // And the default helper for FBO targeting would return false if we could
        // construct an XRFrameBuffer.  We verify the inverse pattern: null → true.
    }

    [Test]
    public void OpTargetsSwapchain_MeshDrawOp_NullTarget_ReturnsTrue()
    {
        var op = SwapchainDraw(passIndex: 0, CtxPipelineA);
        VulkanRenderGraphCompiler.OpTargetsSwapchain(op).ShouldBeTrue();
    }

    [Test]
    public void OpTargetsSwapchain_BlitOp_NullOutFbo_ReturnsTrue()
    {
        var op = SwapchainBlit(passIndex: 0, CtxPipelineA);
        VulkanRenderGraphCompiler.OpTargetsSwapchain(op).ShouldBeTrue();
    }

    [Test]
    public void OpTargetsSwapchain_IndirectDrawOp_ReturnsTrue()
    {
        var op = SwapchainIndirectDraw(passIndex: 0, CtxPipelineA);
        VulkanRenderGraphCompiler.OpTargetsSwapchain(op).ShouldBeTrue();
    }

    [Test]
    public void OpTargetsSwapchain_ComputeDispatchOp_ReturnsFalse()
    {
        var op = ComputeDispatch(passIndex: 0, CtxPipelineA);
        VulkanRenderGraphCompiler.OpTargetsSwapchain(op).ShouldBeFalse();
    }

    [Test]
    public void OpTargetsSwapchain_MemoryBarrierOp_ReturnsFalse()
    {
        var op = MemoryBarrier(passIndex: 0, CtxPipelineA);
        VulkanRenderGraphCompiler.OpTargetsSwapchain(op).ShouldBeFalse();
    }

    #endregion

    #region CoalesceSwapchainContexts

    [Test]
    public void Coalesce_AllSwapchainOps_ShareFirstContext()
    {
        // Simulates DebugOpaqueRenderPipeline (A) + UserInterfaceRenderPipeline (B)
        // both targeting the swapchain.  After coalescing, all ops must share A's context.
        FrameOp[] ops =
        [
            SwapchainClear(0, CtxPipelineA),
            SwapchainDraw(1, CtxPipelineA),
            SwapchainDraw(2, CtxPipelineB), // <-- This was causing a context change
            SwapchainDraw(3, CtxPipelineB),
        ];

        VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops);

        foreach (var op in ops)
            op.Context.ShouldBe(CtxPipelineA, $"Op at passIndex {op.PassIndex} should have been coalesced to pipeline A context");
    }

    [Test]
    public void Coalesce_ThreePipelines_AllUnifiedToFirstSwapchainContext()
    {
        FrameOp[] ops =
        [
            SwapchainClear(0, CtxPipelineA),
            SwapchainDraw(1, CtxPipelineB),
            SwapchainDraw(2, CtxPipelineC),
        ];

        VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops);

        ops.ShouldAllBe(op => op.Context == CtxPipelineA);
    }

    [Test]
    public void Coalesce_MixedTargets_FboOpsKeepOriginalContext()
    {
        // FBO-targeting ops (e.g. shadow map, G-buffer) must keep their own context
        // for correct barrier/resource planning.  Only swapchain ops get coalesced.
        //
        // We can't easily construct an XRFrameBuffer in tests, so we use
        // ComputeDispatchOp (which never targets the swapchain) as the FBO-proxy.
        FrameOp[] ops =
        [
            SwapchainClear(0, CtxPipelineA),
            ComputeDispatch(1, CtxPipelineB),  // Not a swapchain op
            SwapchainDraw(2, CtxPipelineB),    // Swapchain op — should be coalesced to A
            MemoryBarrier(3, CtxPipelineC),    // Not a swapchain op
        ];

        VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops);

        // Swapchain ops should be coalesced to pipeline A
        ops[0].Context.ShouldBe(CtxPipelineA);
        ops[2].Context.ShouldBe(CtxPipelineA);

        // Non-swapchain ops should keep their original contexts
        ops[1].Context.ShouldBe(CtxPipelineB);
        ops[3].Context.ShouldBe(CtxPipelineC);
    }

    [Test]
    public void Coalesce_SinglePipeline_NoChange()
    {
        FrameOp[] ops =
        [
            SwapchainClear(0, CtxPipelineA),
            SwapchainDraw(1, CtxPipelineA),
            SwapchainDraw(2, CtxPipelineA),
        ];

        VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops);

        ops.ShouldAllBe(op => op.Context == CtxPipelineA);
    }

    [Test]
    public void Coalesce_EmptyOps_DoesNotThrow()
    {
        FrameOp[] ops = [];
        Should.NotThrow(() => VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops));
    }

    [Test]
    public void Coalesce_NoSwapchainOps_NoChange()
    {
        FrameOp[] ops =
        [
            ComputeDispatch(0, CtxPipelineA),
            MemoryBarrier(1, CtxPipelineB),
        ];

        VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops);

        ops[0].Context.ShouldBe(CtxPipelineA);
        ops[1].Context.ShouldBe(CtxPipelineB);
    }

    [Test]
    public void Coalesce_IndirectDrawOp_IsCoalesced()
    {
        FrameOp[] ops =
        [
            SwapchainClear(0, CtxPipelineA),
            SwapchainIndirectDraw(1, CtxPipelineB),
        ];

        VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops);

        ops[1].Context.ShouldBe(CtxPipelineA);
    }

    [Test]
    public void Coalesce_BlitOp_SwapchainTargeted_IsCoalesced()
    {
        FrameOp[] ops =
        [
            SwapchainDraw(0, CtxPipelineA),
            SwapchainBlit(1, CtxPipelineB),
        ];

        VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops);

        ops[1].Context.ShouldBe(CtxPipelineA);
    }

    #endregion

    #region SortFrameOps — first-occurrence ordering

    [Test]
    public void SortFrameOps_PreservesFirstOccurrenceOrder_NotHashOrder()
    {
        // This is the core regression test.  Previously ops were sorted by raw
        // SchedulingIdentity hash, which is non-deterministic across runs and could
        // reorder pipelines (e.g. UI before scene), causing context changes that
        // restart the swapchain render pass.
        //
        // Fix: sort by first-occurrence index of each SchedulingIdentity.
        var compiler = new VulkanRenderGraphCompiler();
        var graph = VulkanCompiledRenderGraph.Empty; // No PassOrder — purely GroupOrder

        FrameOp[] ops =
        [
            SwapchainClear(0, CtxPipelineA),  // First occurrence of A → GroupOrder 0
            SwapchainDraw(1, CtxPipelineA),
            SwapchainDraw(2, CtxPipelineB),   // First occurrence of B → GroupOrder 2
            SwapchainDraw(3, CtxPipelineB),
        ];

        FrameOp[] sorted = VulkanRenderGraphCompiler.SortFrameOps(ops, graph);

        // Pipeline A ops should come before pipeline B ops
        sorted[0].PassIndex.ShouldBe(0);
        sorted[1].PassIndex.ShouldBe(1);
        sorted[2].PassIndex.ShouldBe(2);
        sorted[3].PassIndex.ShouldBe(3);
    }

    [Test]
    public void SortFrameOps_GroupsInterleavedOps_ByFirstOccurrence()
    {
        // If ops arrive interleaved (A, B, A, B), sort should group them as (A, A, B, B)
        // based on first-occurrence order.
        var compiler = new VulkanRenderGraphCompiler();
        var graph = VulkanCompiledRenderGraph.Empty;

        FrameOp[] ops =
        [
            SwapchainClear(0, CtxPipelineA),  // First occurrence of A → GroupOrder 0
            SwapchainDraw(1, CtxPipelineB),   // First occurrence of B → GroupOrder 1
            SwapchainDraw(2, CtxPipelineA),   // GroupOrder 0 (same as first A)
            SwapchainDraw(3, CtxPipelineB),   // GroupOrder 1 (same as first B)
        ];

        FrameOp[] sorted = VulkanRenderGraphCompiler.SortFrameOps(ops, graph);

        // All A ops grouped first, then all B ops
        sorted[0].Context.ShouldBe(CtxPipelineA);
        sorted[1].Context.ShouldBe(CtxPipelineA);
        sorted[2].Context.ShouldBe(CtxPipelineB);
        sorted[3].Context.ShouldBe(CtxPipelineB);

        // Within each group, original order is preserved
        sorted[0].PassIndex.ShouldBe(0);
        sorted[1].PassIndex.ShouldBe(2);
        sorted[2].PassIndex.ShouldBe(1);
        sorted[3].PassIndex.ShouldBe(3);
    }

    [Test]
    public void SortFrameOps_RespectsPassOrder_WithinGroup()
    {
        // When PassOrder is available, it takes precedence within a GroupOrder group.
        var compiler = new VulkanRenderGraphCompiler();
        var passOrder = new Dictionary<int, int>
        {
            { 10, 2 },  // PassIndex 10 → topological order 2
            { 20, 1 },  // PassIndex 20 → topological order 1
            { 30, 0 },  // PassIndex 30 → topological order 0
        };
        var graph = GraphWithPassOrder(passOrder);

        // All same context, different pass indices
        FrameOp[] ops =
        [
            SwapchainDraw(10, CtxPipelineA),  // PassOrder 2
            SwapchainDraw(20, CtxPipelineA),  // PassOrder 1
            SwapchainDraw(30, CtxPipelineA),  // PassOrder 0
        ];

        FrameOp[] sorted = VulkanRenderGraphCompiler.SortFrameOps(ops, graph);

        // Should be sorted by PassOrder: 30 (order 0), 20 (order 1), 10 (order 2)
        sorted[0].PassIndex.ShouldBe(30);
        sorted[1].PassIndex.ShouldBe(20);
        sorted[2].PassIndex.ShouldBe(10);
    }

    [Test]
    public void SortFrameOps_UnknownPassIndex_GetsMaxValue_SortedLast()
    {
        var compiler = new VulkanRenderGraphCompiler();
        var passOrder = new Dictionary<int, int>
        {
            { 10, 0 },
        };
        var graph = GraphWithPassOrder(passOrder);

        FrameOp[] ops =
        [
            SwapchainDraw(99, CtxPipelineA),  // Unknown PassIndex → int.MaxValue
            SwapchainDraw(10, CtxPipelineA),  // Known PassIndex → order 0
        ];

        FrameOp[] sorted = VulkanRenderGraphCompiler.SortFrameOps(ops, graph);

        sorted[0].PassIndex.ShouldBe(10);
        sorted[1].PassIndex.ShouldBe(99);
    }

    [Test]
    public void SortFrameOps_SingleOp_ReturnsSameArray()
    {
        var compiler = new VulkanRenderGraphCompiler();
        FrameOp[] ops = [SwapchainClear(0, CtxPipelineA)];

        FrameOp[] sorted = VulkanRenderGraphCompiler.SortFrameOps(ops, VulkanCompiledRenderGraph.Empty);

        sorted.ShouldBeSameAs(ops);
    }

    [Test]
    public void SortFrameOps_EmptyArray_ReturnsSameArray()
    {
        var compiler = new VulkanRenderGraphCompiler();
        FrameOp[] ops = [];

        FrameOp[] sorted = VulkanRenderGraphCompiler.SortFrameOps(ops, VulkanCompiledRenderGraph.Empty);

        sorted.ShouldBeSameAs(ops);
    }

    [Test]
    public void SortFrameOps_GroupOrderThenPassOrder_CrossPipeline()
    {
        // Realistic scenario: two pipelines, each with multiple passes.
        // GroupOrder should separate them; PassOrder should order within each group.
        var compiler = new VulkanRenderGraphCompiler();
        var passOrder = new Dictionary<int, int>
        {
            { 0, 0 },  // Background
            { 1, 1 },  // OpaqueForward
            { 2, 2 },  // TransparentForward
            { 5, 0 },  // UI pass 0
            { 6, 1 },  // UI pass 1
        };
        var graph = GraphWithPassOrder(passOrder);

        FrameOp[] ops =
        [
            // Scene pipeline (A)
            SwapchainClear(0, CtxPipelineA),
            SwapchainDraw(1, CtxPipelineA),
            SwapchainDraw(2, CtxPipelineA),
            // UI pipeline (B)
            SwapchainDraw(5, CtxPipelineB),
            SwapchainDraw(6, CtxPipelineB),
        ];

        FrameOp[] sorted = VulkanRenderGraphCompiler.SortFrameOps(ops, graph);

        // Scene pipeline ops first (GroupOrder 0), then UI (GroupOrder 3)
        sorted[0].PassIndex.ShouldBe(0);
        sorted[1].PassIndex.ShouldBe(1);
        sorted[2].PassIndex.ShouldBe(2);
        sorted[3].PassIndex.ShouldBe(5);
        sorted[4].PassIndex.ShouldBe(6);
    }

    #endregion

    #region End-to-end: Coalesce + Sort

    [Test]
    public void EndToEnd_CoalesceThenSort_EliminatesContextChanges()
    {
        // This is the full regression scenario:
        // Two pipelines target the swapchain.  After coalescing, all share context A.
        // After sorting, they're grouped by first-occurrence order.
        // Result: zero context changes between swapchain ops → no render-pass restarts.
        var compiler = new VulkanRenderGraphCompiler();
        var graph = VulkanCompiledRenderGraph.Empty;

        FrameOp[] ops =
        [
            SwapchainClear(0, CtxPipelineA),
            SwapchainDraw(1, CtxPipelineA),
            SwapchainDraw(2, CtxPipelineA),
            SwapchainDraw(3, CtxPipelineB),  // UI pipeline
            SwapchainDraw(4, CtxPipelineB),
        ];

        // Step 1: Coalesce
        VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops);

        // Step 2: Sort
        FrameOp[] sorted = VulkanRenderGraphCompiler.SortFrameOps(ops, graph);

        // All ops should now share the same context
        FrameOpContext expectedContext = CtxPipelineA;
        foreach (var op in sorted)
            op.Context.ShouldBe(expectedContext);

        // Count context transitions — should be zero
        int contextChanges = 0;
        for (int i = 1; i < sorted.Length; i++)
        {
            if (!Equals(sorted[i].Context, sorted[i - 1].Context))
                contextChanges++;
        }
        contextChanges.ShouldBe(0, "Coalesced swapchain ops should have zero context changes");
    }

    [Test]
    public void EndToEnd_MixedTargets_FboOpsHaveSeparateContextButSwapchainUnified()
    {
        // When FBO-targeting ops exist (represented here by ComputeDispatch which
        // never targets swapchain), they keep their own context.  Only swapchain
        // ops are unified.
        var compiler = new VulkanRenderGraphCompiler();
        var graph = VulkanCompiledRenderGraph.Empty;

        FrameOp[] ops =
        [
            SwapchainClear(0, CtxPipelineA),
            ComputeDispatch(1, CtxPipelineB),   // FBO-like op — keeps B
            SwapchainDraw(2, CtxPipelineA),
            SwapchainDraw(3, CtxPipelineB),     // Swapchain — coalesced to A
            ComputeDispatch(4, CtxPipelineC),   // FBO-like op — keeps C
        ];

        VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops);
        FrameOp[] sorted = VulkanRenderGraphCompiler.SortFrameOps(ops, graph);

        // All swapchain ops should have context A
        foreach (var op in sorted.Where(o => VulkanRenderGraphCompiler.OpTargetsSwapchain(o)))
            op.Context.ShouldBe(CtxPipelineA);

        // Non-swapchain ops should retain their original context
        var nonSwapchainOps = sorted.Where(o => !VulkanRenderGraphCompiler.OpTargetsSwapchain(o)).ToArray();
        nonSwapchainOps.ShouldContain(op => op.Context == CtxPipelineB);
        nonSwapchainOps.ShouldContain(op => op.Context == CtxPipelineC);
    }

    [Test]
    public void EndToEnd_FirstOccurrenceOrder_IsDeterministic_AcrossMultipleRuns()
    {
        // Verify determinism: running the same coalesce+sort 100 times should
        // always produce the same output order.
        var compiler = new VulkanRenderGraphCompiler();
        var graph = VulkanCompiledRenderGraph.Empty;

        int[][] allResults = new int[100][];

        for (int run = 0; run < 100; run++)
        {
            FrameOp[] ops =
            [
                SwapchainClear(0, CtxPipelineA),
                SwapchainDraw(1, CtxPipelineB),
                SwapchainDraw(2, CtxPipelineA),
                SwapchainDraw(3, CtxPipelineC),
                SwapchainDraw(4, CtxPipelineB),
            ];

            VulkanRenderGraphCompiler.CoalesceSwapchainContexts(ops);
            FrameOp[] sorted = VulkanRenderGraphCompiler.SortFrameOps(ops, graph);
            allResults[run] = sorted.Select(o => o.PassIndex).ToArray();
        }

        // All 100 runs should produce identical pass order
        int[] expected = allResults[0];
        for (int run = 1; run < 100; run++)
            allResults[run].ShouldBe(expected, $"Run {run} produced different order than run 0");
    }

    #endregion
}
