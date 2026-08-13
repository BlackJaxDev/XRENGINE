using XREngine.Rendering;
using XREngine.Rendering.Profiling;

namespace XREngine.RenderBench;

/// <summary>Phase 4 deterministic fixture catalog. Names are stable recipe API.</summary>
public static class RenderBenchFixtureCatalog
{
    private static readonly RenderExecutionMode[] s_component = [RenderExecutionMode.Component];
    private static readonly RenderExecutionMode[] s_both = [RenderExecutionMode.Component, RenderExecutionMode.Presentationless];
    private static readonly RenderExecutionMode[] s_presentationless = [RenderExecutionMode.Presentationless];
    private static readonly string[] s_cpuNativeSubmitGpu = ["managed fixture preparation", "native Vulkan recording", "queue submission", "GPU execution"];
    private static readonly string[] s_noPresentation = ["window creation", "surface/swapchain presentation", "editor/world state"];
    private static readonly string[] s_gpuPassExclusions = ["production scene traversal", "window presentation", "unrelated render passes"];

    public static IReadOnlyList<RenderBenchFixtureDefinition> Definitions { get; } =
    [
        new("synthetic-clear", "SyntheticClearSubmission", RenderBenchFixtureKind.Control, s_both,
            s_cpuNativeSubmitGpu, s_noPresentation, DefaultBarrierCount: 2),
        new("synthetic-clear", "PresentationlessFrame", RenderBenchFixtureKind.Control, s_presentationless,
            s_cpuNativeSubmitGpu, s_noPresentation, DefaultBarrierCount: 2),
        new("noop-control", "HarnessSubmission", RenderBenchFixtureKind.Control, s_both,
            ["frame-slot acquire/complete", "primary command buffer", "queue submission", "GPU completion"], s_noPresentation,
            DefaultBarrierCount: 2),
        new("command-chain-signature", "CommandChainSignature", RenderBenchFixtureKind.CommandChainSignature, s_component,
            ["immutable command-chain signature hashing", "harness control submission"], s_noPresentation,
            DefaultChainCount: 256),
        new("packet-lowering", "PacketLowering", RenderBenchFixtureKind.PacketLowering, s_component,
            ["immutable packet lowering", "harness control submission"], s_noPresentation,
            DefaultChainCount: 256),
        new("primary-command-small", "PrimaryCommandEncoding", RenderBenchFixtureKind.PrimaryCommandEncoding, s_component,
            ["primary native Vulkan command encoding", "queue submission", "GPU execution"], s_noPresentation,
            DefaultBarrierCount: 64),
        new("primary-command-medium", "PrimaryCommandEncoding", RenderBenchFixtureKind.PrimaryCommandEncoding, s_component,
            ["primary native Vulkan command encoding", "queue submission", "GPU execution"], s_noPresentation,
            DefaultBarrierCount: 512),
        new("primary-command-large", "PrimaryCommandEncoding", RenderBenchFixtureKind.PrimaryCommandEncoding, s_component,
            ["primary native Vulkan command encoding", "queue submission", "GPU execution"], s_noPresentation,
            DefaultBarrierCount: 4096),
        new("secondary-command-recording", "SecondaryCommandRecording", RenderBenchFixtureKind.SecondaryCommandRecording, s_component,
            ["worker-owned secondary recording", "primary execute-commands", "queue submission", "GPU execution"], s_noPresentation,
            DefaultChainCount: 32, DefaultBarrierCount: 8),
        new("command-buffer-stable-reuse", "CommandBufferReuse", RenderBenchFixtureKind.CommandBufferReuse, s_component,
            ["pre-recorded secondary reuse decision", "primary execute-commands", "queue submission"], s_noPresentation,
            DefaultChainCount: 32, DefaultBarrierCount: 8),
        new("command-buffer-forced-dirty", "CommandBufferReuse", RenderBenchFixtureKind.CommandBufferReuse, s_component,
            ["forced-dirty secondary recording", "primary execute-commands", "queue submission"], s_noPresentation,
            DefaultChainCount: 32, DefaultBarrierCount: 8),
        new("descriptor-publication", "DescriptorPublication", RenderBenchFixtureKind.DescriptorPublication, s_component,
            ["precreated descriptor layout/pool/resources", "descriptor publication/update", "control submission"], s_noPresentation,
            DefaultDescriptorCount: 8),
        new("resource-planning", "ResourcePlanningAndBarriers", RenderBenchFixtureKind.ResourcePlanning, s_component,
            ["fixed immutable dependency graph", "native image-layout/barrier encoding", "queue submission"], s_noPresentation,
            DefaultBarrierCount: 128),
        new("queue-lock-submit", "QueueLockAndSubmit", RenderBenchFixtureKind.QueueSubmission, s_component,
            ["frame-slot acquire", "queue gateway/lock", "one minimal queue submission", "GPU completion"], s_noPresentation,
            DefaultBarrierCount: 2),
        new("upload-fixed", "FixedUpload", RenderBenchFixtureKind.Upload, s_component,
            ["precreated resident staging/device buffers", "fixed-size buffer copy", "queue submission", "GPU execution"], s_noPresentation,
            DefaultUploadBytes: 1_048_576),
        GpuPass("gpu-shadow", "ShadowPass"),
        GpuPass("gpu-depth-normal", "DepthNormalPass"),
        GpuPass("gpu-gbuffer", "GBufferPass"),
        GpuPass("gpu-lighting", "LightingPass"),
        GpuPass("gpu-transparency", "TransparencyPass"),
        GpuPass("gpu-ao", "AmbientOcclusionPass"),
        GpuPass("gpu-bloom", "BloomPass"),
        GpuPass("gpu-tsr", "TsrPass"),
        GpuPass("gpu-final-composition", "FinalCompositionPass"),
        new("presentationless-deferred", "DeferredFrame", RenderBenchFixtureKind.FullPresentationless, s_presentationless,
            ["canonical Deferred pass sequence", "native Vulkan draws", "queue submission", "GPU execution", "presentationless output"],
            ["window/swapchain presentation", "editor UI", "scene streaming"], DefaultDrawCount: 9, DefaultBarrierCount: 18, DefaultPassIterations: 9),
        new("presentationless-uber", "UberFrame", RenderBenchFixtureKind.FullPresentationless, s_presentationless,
            ["canonical Uber pass sequence", "native Vulkan draws", "queue submission", "GPU execution", "presentationless output"],
            ["window/swapchain presentation", "editor UI", "scene streaming"], DefaultDrawCount: 6, DefaultBarrierCount: 12, DefaultPassIterations: 6),
    ];

    public static RenderBenchFixtureDefinition Get(string name, string component, RenderExecutionMode mode)
        => Definitions.FirstOrDefault(definition =>
               definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
               definition.Component.Equals(component, StringComparison.OrdinalIgnoreCase) &&
               definition.ExecutionModes.Contains(mode))
           ?? throw new NotSupportedException($"No deterministic fixture '{name}' targets component '{component}' in mode '{mode}'.");

    public static IRenderBenchFixture Create(RenderProfileRecipe recipe)
        => new SyntheticRenderBenchFixture(Get(recipe.Fixture, recipe.Component, recipe.ExecutionMode), recipe);

    private static RenderBenchFixtureDefinition GpuPass(string name, string component)
        => new(name, component, RenderBenchFixtureKind.GpuPass, s_both,
            ["precreated fullscreen pipeline", "one native Vulkan render pass", "queue submission", "GPU execution"],
            s_gpuPassExclusions, DefaultDrawCount: 1, DefaultBarrierCount: 2);
}
