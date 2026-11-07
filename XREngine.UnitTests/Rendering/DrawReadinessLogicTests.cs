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
    public void Registry_GivesNotStarted_UntilAFrameEvaluatesTheRequest_ThenFollowsEachFrame()
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        registry.HasActiveRequests.ShouldBeFalse();

        // The registry compares the renderer by reference only, so a test can use a null renderer key.
        registry.Observe(null!, material, null, stereo: false, nowTicks: 1000, out _).ShouldBe(ERenderDrawReadiness.NotStarted);
        registry.HasActiveRequests.ShouldBeTrue();

        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: 1100);
        active.Count.ShouldBe(1);
        registry.Publish(active[0], ERenderDrawReadiness.Ready, "Ready");
        registry.Observe(null!, material, null, stereo: false, nowTicks: 1200, out _).ShouldBe(ERenderDrawReadiness.Ready);

        registry.Publish(active[0], ERenderDrawReadiness.Compiling, "a new variant compiles");
        registry.Observe(null!, material, null, stereo: false, nowTicks: 1300, out string reason).ShouldBe(ERenderDrawReadiness.Compiling);
        reason.ShouldBe("a new variant compiles");
    }

    [Test]
    public void Registry_EndsARequestThatNoQueryRenewed()
    {
        VulkanDrawReadinessRegistry registry = new();
        XRMaterial material = new() { Name = "Proxy" };
        registry.Observe(null!, material, null, stereo: false, nowTicks: 0, out _);

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
        registry.Observe(null!, material, null, stereo: false, nowTicks: 0, out _);
        registry.Observe(null!, material, null, stereo: true, nowTicks: 0, out _);

        List<VulkanDrawReadinessRequest> active = [];
        registry.CopyActive(active, nowTicks: 10);
        active.Count.ShouldBe(2);
        active.Count(r => r.Stereo).ShouldBe(1);
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
