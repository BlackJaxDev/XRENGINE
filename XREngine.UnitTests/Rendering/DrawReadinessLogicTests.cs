using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Covers the backend-independent parts of the draw readiness query: the state combiner, the Vulkan context tally and
/// request registry, the shared Vulkan draw state rules, and the result classifiers. The OpenGL and Vulkan query bodies need
/// a GL context or a Vulkan device, which the unit tests do not have, so the live check covers them.
/// </summary>
[TestFixture]
public sealed class DrawReadinessLogicTests
{
    [TestCase(ERenderDrawReadiness.Ready, ERenderDrawReadiness.Ready, ERenderDrawReadiness.Ready)]
    [TestCase(ERenderDrawReadiness.Ready, ERenderDrawReadiness.Compiling, ERenderDrawReadiness.Compiling)]
    [TestCase(ERenderDrawReadiness.Compiling, ERenderDrawReadiness.NotStarted, ERenderDrawReadiness.NotStarted)]
    [TestCase(ERenderDrawReadiness.NotSupported, ERenderDrawReadiness.NotStarted, ERenderDrawReadiness.NotSupported)]
    [TestCase(ERenderDrawReadiness.Ready, ERenderDrawReadiness.Failed, ERenderDrawReadiness.Failed)]
    [TestCase(ERenderDrawReadiness.Failed, ERenderDrawReadiness.NotSupported, ERenderDrawReadiness.Failed)]
    public void Combiner_IsReadyOnlyWhenBothPartsAreReady(ERenderDrawReadiness mono, ERenderDrawReadiness stereo, ERenderDrawReadiness expected)
    {
        RenderDrawReadinessCombiner.Combine(mono, "mono", stereo, "stereo", out string reason).ShouldBe(expected);
        reason.ShouldBe(expected == mono ? "mono" : "stereo");
    }

    [Test]
    public void Tally_WithNoRequiredContext_IsNotStarted()
    {
        VulkanDrawReadinessTally tally = default;
        tally.ToState("no context", out string reason).ShouldBe(ERenderDrawReadiness.NotStarted);
        reason.ShouldBe("no context");
    }

    [Test]
    public void Tally_IsReadyOnlyWhenEveryRequiredContextIsReady()
    {
        VulkanDrawReadinessTally tally = default;
        tally.Add(ready: true, retryable: false, "Ready");
        tally.Add(ready: false, retryable: true, "graphics pipeline compile is queued");
        tally.ToState("no context", out string reason).ShouldBe(ERenderDrawReadiness.Compiling);
        reason.ShouldBe("graphics pipeline compile is queued");

        VulkanDrawReadinessTally allReady = default;
        allReady.Add(ready: true, retryable: false, "Ready");
        allReady.Add(ready: true, retryable: false, "Ready");
        allReady.ToState("no context", out _).ShouldBe(ERenderDrawReadiness.Ready);
        allReady.Required.ShouldBe(2);
        allReady.Ready.ShouldBe(2);
    }

    [Test]
    public void Tally_ANonRetryableResult_IsFailed_WithItsOwnReason()
    {
        VulkanDrawReadinessTally tally = default;
        tally.Add(ready: false, retryable: true, "compiling in the opaque pass");
        tally.Add(ready: false, retryable: false, "pipeline creation failed in the forward pass");
        tally.Add(ready: false, retryable: true, "compiling in another pass");
        tally.ToState("no context", out string reason).ShouldBe(ERenderDrawReadiness.Failed);
        reason.ShouldBe("pipeline creation failed in the forward pass");
    }

    [Test]
    public void Registry_AReadyOfAnotherRetirementGeneration_IsCompiling()
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 0, currentRetirementGeneration: 5, out _, out _, out _);
        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: 1);
        registry.Publish(active[0], ERenderDrawReadiness.Ready, "Ready", null, 0UL, retirementGeneration: 5, targetSignature: 9UL);

        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 2, currentRetirementGeneration: 5, out _, out _, out _)
            .ShouldBe(ERenderDrawReadiness.Ready);

        // A retirement wave removed the measured pipelines.
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 3, currentRetirementGeneration: 6, out string reason, out _, out _)
            .ShouldBe(ERenderDrawReadiness.Compiling);
        reason.ShouldContain("changed");
    }

    [TestCase(6L, 9UL)]
    [TestCase(5L, 10UL)]
    public void Registry_AKeptReady_BecomesCompiling_WhenThePipelinesOrTheTargetsChange(long retirementGeneration, ulong targetSignature)
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 0, currentRetirementGeneration: 5, out _, out _, out _);
        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: 1);
        registry.Publish(active[0], ERenderDrawReadiness.Ready, "Ready", null, 0UL, retirementGeneration: 5, targetSignature: 9UL);

        // A plan with no context of the material: same generation and target keep the Ready.
        registry.NoteNoContext(active[0], "no context", retirementGeneration: 5, targetSignature: 9UL);
        active[0].State.ShouldBe(ERenderDrawReadiness.Ready);

        // A resize, an MSAA change, or a retirement wave in that window: the kept Ready is stale.
        registry.NoteNoContext(active[0], "no context", retirementGeneration, targetSignature);
        active[0].State.ShouldBe(ERenderDrawReadiness.Compiling);
    }

    [Test]
    public void Registry_StoresThePassStateOfEachPassContext_AndClearsItWithTheLastRequest()
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        VulkanFixedFunctionStateSnapshot opaque = OpaquePassState();
        VulkanFixedFunctionStateSnapshot forward = opaque with { BlendEnabled = true };
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 0, currentRetirementGeneration: 0, out _, out _, out _);

        registry.RecordPassState(null, 3, null, opaque);
        registry.RecordPassState(null, 4, null, forward);

        registry.TryGetPassState(null, 3, null, out VulkanFixedFunctionStateSnapshot found).ShouldBeTrue();
        found.ShouldBe(opaque);
        registry.TryGetPassState(null, 4, null, out found).ShouldBeTrue();
        found.ShouldBe(forward);
        registry.TryGetPassState(null, 5, null, out _).ShouldBeFalse();

        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: VulkanDrawReadinessRegistry.RequestLifetimeMilliseconds + 1);
        registry.TryGetPassState(null, 3, null, out _).ShouldBeFalse();
    }

    [Test]
    public void Registry_APlanWithNoContext_NeverReplacesAMeasuredResult()
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 0, currentRetirementGeneration: 0, out _, out _, out _);
        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: 1);

        registry.NoteNoContext(active[0], "no context", retirementGeneration: 0, targetSignature: 0UL);
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 2, currentRetirementGeneration: 0, out string reason, out _, out _)
            .ShouldBe(ERenderDrawReadiness.NotStarted);
        reason.ShouldBe("no context");

        registry.Publish(active[0], ERenderDrawReadiness.Ready, "Ready", null, 0UL, retirementGeneration: 0, targetSignature: 0UL);
        registry.NoteNoContext(active[0], "no context", retirementGeneration: 0, targetSignature: 0UL);
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 3, currentRetirementGeneration: 0, out reason, out _, out _)
            .ShouldBe(ERenderDrawReadiness.Ready);
        reason.ShouldBe("Ready");
    }

    [Test]
    public void Registry_GivesNotStarted_UntilAFrameEvaluatesTheRequest_ThenFollowsEachFrame()
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        registry.HasActiveRequests.ShouldBeFalse();

        // The registry compares the renderer by reference only, so a test can use a null renderer key.
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 1000, currentRetirementGeneration: 0, out _, out _, out _).ShouldBe(ERenderDrawReadiness.NotStarted);
        registry.HasActiveRequests.ShouldBeTrue();

        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: 1100);
        active.Count.ShouldBe(1);
        registry.Publish(active[0], ERenderDrawReadiness.Ready, "Ready", null, 0UL, retirementGeneration: 0, targetSignature: 0UL);
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 1200, currentRetirementGeneration: 0, out _, out _, out _).ShouldBe(ERenderDrawReadiness.Ready);

        registry.Publish(active[0], ERenderDrawReadiness.Compiling, "a new variant compiles", null, 0UL, retirementGeneration: 0, targetSignature: 0UL);
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 1300, currentRetirementGeneration: 0, out string reason, out _, out _).ShouldBe(ERenderDrawReadiness.Compiling);
        reason.ShouldBe("a new variant compiles");
    }

    [Test]
    public void Registry_EndsARequestThatNoQueryRenewed()
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 0, currentRetirementGeneration: 0, out _, out _, out _);

        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: VulkanDrawReadinessRegistry.RequestLifetimeMilliseconds + 1);

        active.ShouldBeEmpty();
        registry.HasActiveRequests.ShouldBeFalse();
    }

    [Test]
    public void Registry_KeepsMonoAndStereoRequestsApart()
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 0, currentRetirementGeneration: 0, out _, out _, out _);
        registry.Observe(null!, material, null, null, stereo: true, nowTicks: 0, currentRetirementGeneration: 0, out _, out _, out _);

        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: 10);
        active.Count.ShouldBe(2);
        active.Count(r => r.Stereo).ShouldBe(1);
    }

    [Test]
    public void Registry_GivesThePublishedProgram_AndAReadyOfAnotherProgramIsCompiling()
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        object canonicalProgram = new();
        object variantProgram = new();
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 0, currentRetirementGeneration: 0, out _, out _, out _);
        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: 1);
        registry.Publish(active[0], ERenderDrawReadiness.Ready, "Ready", canonicalProgram, 3UL, retirementGeneration: 0, targetSignature: 0UL);

        ERenderDrawReadiness state = registry.Observe(
            null!, material, null, null, stereo: false, nowTicks: 2, currentRetirementGeneration: 0, out string reason, out object? program, out ulong generation);
        state.ShouldBe(ERenderDrawReadiness.Ready);
        program.ShouldBeSameAs(canonicalProgram);
        generation.ShouldBe(3UL);

        // Same program and generation: ready.
        VulkanDrawReadinessRegistry.ResolvePublishedState(state, reason, program, generation, canonicalProgram, 3UL, out _)
            .ShouldBe(ERenderDrawReadiness.Ready);

        // The variant activated: another program is active, so the published ready is stale.
        VulkanDrawReadinessRegistry.ResolvePublishedState(state, reason, program, generation, variantProgram, 1UL, out string stale)
            .ShouldBe(ERenderDrawReadiness.Compiling);
        stale.ShouldContain("program changed");

        // A relink of the same program: a new link generation is stale too.
        VulkanDrawReadinessRegistry.ResolvePublishedState(state, reason, program, generation, canonicalProgram, 4UL, out _)
            .ShouldBe(ERenderDrawReadiness.Compiling);
    }

    [TestCase(ERenderDrawReadiness.Compiling)]
    [TestCase(ERenderDrawReadiness.NotStarted)]
    [TestCase(ERenderDrawReadiness.Failed)]
    public void ResolvePublishedState_KeepsANotReadyState_WhenTheProgramChanged(ERenderDrawReadiness published)
        => VulkanDrawReadinessRegistry.ResolvePublishedState(published, "r", new object(), 1UL, new object(), 2UL, out _)
            .ShouldBe(published);

    [Test]
    public void Registry_RemoveRenderer_EndsItsRequestsAtOnce()
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 0, currentRetirementGeneration: 0, out _, out _, out _);
        registry.Observe(null!, material, null, null, stereo: true, nowTicks: 0, currentRetirementGeneration: 0, out _, out _, out _);
        registry.HasActiveRequests.ShouldBeTrue();

        registry.RemoveRenderer(null!);

        registry.HasActiveRequests.ShouldBeFalse();
        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: 1);
        active.ShouldBeEmpty();
    }

    [Test]
    public void Registry_KeepsRequestsWithAnotherRenderOptionsOverrideApart()
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        XREngine.Rendering.Models.Materials.RenderingParameters options = new();
        registry.Observe(null!, material, null, null, stereo: false, nowTicks: 0, currentRetirementGeneration: 0, out _, out _, out _);
        registry.Observe(null!, material, options, null, stereo: false, nowTicks: 0, currentRetirementGeneration: 0, out _, out _, out _);

        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: 1);
        active.Count.ShouldBe(2);
        active.Count(r => ReferenceEquals(r.RenderOptionsOverride, options)).ShouldBe(1);
    }

    private static VulkanFixedFunctionStateSnapshot OpaquePassState()
        => new(
            DepthTestEnabled: true,
            DepthWriteEnabled: true,
            DepthCompareOp: CompareOp.GreaterOrEqual,
            StencilTestEnabled: false,
            FrontStencilState: default,
            BackStencilState: default,
            StencilWriteMask: 0u,
            ColorWriteMask: ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            CullMode: CullModeFlags.BackBit,
            FrontFace: FrontFace.CounterClockwise,
            BlendEnabled: false,
            AlphaToCoverageEnabled: false,
            ColorBlendOp: BlendOp.Add,
            AlphaBlendOp: BlendOp.Add,
            SrcColorBlendFactor: BlendFactor.One,
            DstColorBlendFactor: BlendFactor.Zero,
            SrcAlphaBlendFactor: BlendFactor.One,
            DstAlphaBlendFactor: BlendFactor.Zero);

    /// <summary>A template draw whose own material wrote stencil and enabled blend over the pass state.</summary>
    private static PendingMeshDraw StencilBlendTemplate(VulkanFixedFunctionStateSnapshot passState)
        => default(PendingMeshDraw) with
        {
            RasterizationSamples = SampleCountFlags.Count1Bit,
            ViewportScissorCount = 1u,
            DepthTestEnabled = passState.DepthTestEnabled,
            DepthWriteEnabled = passState.DepthWriteEnabled,
            DepthCompareOp = passState.DepthCompareOp,
            StencilTestEnabled = true,
            FrontStencilState = new StencilOpState { PassOp = StencilOp.Replace, CompareOp = CompareOp.Always, WriteMask = 0xFF, Reference = 1 },
            BackStencilState = new StencilOpState { PassOp = StencilOp.Replace, CompareOp = CompareOp.Always, WriteMask = 0xFF, Reference = 1 },
            StencilWriteMask = 0xFF,
            ColorWriteMask = passState.ColorWriteMask,
            CullMode = CullModeFlags.None,
            FrontFace = passState.FrontFace,
            BlendEnabled = true,
            ColorBlendOp = BlendOp.Add,
            AlphaBlendOp = BlendOp.Add,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
        };

    [Test]
    public void TemplateDraw_TakesThePassState_NotTheStencilOrBlendOfTheTemplateMaterial()
    {
        VulkanFixedFunctionStateSnapshot pass = OpaquePassState();
        PendingMeshDraw template = StencilBlendTemplate(pass);

        // A T0 material with default render options leaves stencil unchanged, so the real draw uses the pass stencil.
        XRMaterial t0 = new() { Name = "T0" };
        PendingMeshDraw prewarm = VulkanDrawReadinessTemplate.Build(template, pass, null!, t0, renderOptionsOverride: null);
        VulkanFixedFunctionStateSnapshot real = VulkanMeshDrawFixedState.Resolve(t0.RenderOptions, pass, SampleCountFlags.Count1Bit);

        VulkanDrawReadinessTemplate.GetDrawState(prewarm).ShouldBe(real);
        prewarm.StencilTestEnabled.ShouldBeFalse();
        prewarm.BlendEnabled.ShouldBeFalse();
        prewarm.MaterialOverride.ShouldBeSameAs(t0);

        // The old rule (the T0 options over the resolved template state) gives another key.
        VulkanFixedFunctionStateSnapshot oldRule = VulkanMeshDrawFixedState.Resolve(
            t0.RenderOptions,
            VulkanDrawReadinessTemplate.GetDrawState(template),
            SampleCountFlags.Count1Bit);
        oldRule.ShouldNotBe(real);
    }

    [Test]
    public void TemplateDraw_UsesTheRenderOptionsOverride_InPlaceOfTheMaterialOptions()
    {
        VulkanFixedFunctionStateSnapshot pass = OpaquePassState();
        PendingMeshDraw template = StencilBlendTemplate(pass);
        XRMaterial t0 = new() { Name = "T0" };
        XREngine.Rendering.Models.Materials.RenderingParameters options = new();
        options.CullMode = XREngine.Data.Rendering.ECullMode.None;

        PendingMeshDraw prewarm = VulkanDrawReadinessTemplate.Build(template, pass, null!, t0, options);

        VulkanDrawReadinessTemplate.GetDrawState(prewarm)
            .ShouldBe(VulkanMeshDrawFixedState.Resolve(options, pass, SampleCountFlags.Count1Bit));
    }

    [Test]
    public void DrawState_WithNoMaterialOptions_KeepsThePassState()
    {
        VulkanFixedFunctionStateSnapshot pass = new(
            DepthTestEnabled: true,
            DepthWriteEnabled: false,
            DepthCompareOp: CompareOp.GreaterOrEqual,
            StencilTestEnabled: false,
            FrontStencilState: default,
            BackStencilState: default,
            StencilWriteMask: 0u,
            ColorWriteMask: ColorComponentFlags.RBit | ColorComponentFlags.GBit,
            CullMode: CullModeFlags.FrontBit,
            FrontFace: FrontFace.Clockwise,
            BlendEnabled: true,
            AlphaToCoverageEnabled: true,
            ColorBlendOp: BlendOp.Add,
            AlphaBlendOp: BlendOp.Max,
            SrcColorBlendFactor: BlendFactor.SrcAlpha,
            DstColorBlendFactor: BlendFactor.OneMinusSrcAlpha,
            SrcAlphaBlendFactor: BlendFactor.One,
            DstAlphaBlendFactor: BlendFactor.Zero);

        VulkanMeshDrawFixedState.Resolve(null, pass, SampleCountFlags.Count4Bit).ShouldBe(pass);

        // Alpha to coverage needs more than one sample.
        VulkanMeshDrawFixedState.Resolve(null, pass, SampleCountFlags.Count1Bit).AlphaToCoverageEnabled.ShouldBeFalse();
    }

    [TestCase("DataMissing", ERenderDrawReadiness.Failed)]
    [TestCase("MaterialMissing", ERenderDrawReadiness.Failed)]
    [TestCase("ProgramsPending", ERenderDrawReadiness.Compiling)]
    [TestCase("BuffersPending", ERenderDrawReadiness.Compiling)]
    [TestCase("GenerateFailed", ERenderDrawReadiness.Compiling)]
    public void VulkanPrepareResults_AreClassified(string result, ERenderDrawReadiness expected)
        => VkMeshRenderer.ClassifyPrepareResult(result).ShouldBe(expected);

    [TestCase("GenerateFailed", ERenderDrawReadiness.Failed)]
    [TestCase("MaterialMissing", ERenderDrawReadiness.Failed)]
    [TestCase("NoData", ERenderDrawReadiness.Failed)]
    [TestCase("ProgramsPending", ERenderDrawReadiness.Compiling)]
    [TestCase("BuffersNotReady", ERenderDrawReadiness.Compiling)]
    [TestCase("DeferredOverrun", ERenderDrawReadiness.Compiling)]
    public void OpenGLPrepareResults_AreClassified(string result, ERenderDrawReadiness expected)
        => OpenGLRenderer.GLMeshRenderer.ClassifyPrepareResult(result).ShouldBe(expected);
}
