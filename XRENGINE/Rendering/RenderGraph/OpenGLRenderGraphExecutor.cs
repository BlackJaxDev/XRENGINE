using System;
using System.Collections.Generic;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// OpenGL transition path for the render-graph migration.
/// The OpenGL backend keeps immediate execution semantics while still validating
/// graph topology from the same pass metadata used by Vulkan.
/// </summary>
public sealed class OpenGLRenderGraphExecutor
{
    public static OpenGLRenderGraphExecutor Shared { get; } = new();

    private OpenGLRenderGraphExecutor()
    {
    }

    public void ExecuteSequential(
        ViewportRenderCommandContainer commandChain,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        ArgumentNullException.ThrowIfNull(commandChain);

        if (passMetadata is { Count: > 0 })
        {
            // Force a topological walk so dependency cycles/missing edges are caught
            // on the same metadata path Vulkan uses for compilation.
            _ = RenderGraphSynchronizationPlanner.TopologicallySort(passMetadata);
        }

        commandChain.Execute();
    }
}
