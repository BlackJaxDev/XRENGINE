using XREngine.Rendering.Profiling;
using XREngine.Runtime.Automation.Profiling;

namespace XREngine.RenderBench;

/// <summary>Creates isolated Vulkan control-fixture executors for the runtime MCP control plane.</summary>
public sealed class RenderBenchProfileExecutorFactory(RenderBenchOptions options, RenderBenchProcessState state)
    : IRenderProfileExecutorFactory
{
    public IRenderProfileExecutor Create(RenderProfileRecipe recipe)
        => new RenderBenchProfileExecutor(options, state, recipe);
}
