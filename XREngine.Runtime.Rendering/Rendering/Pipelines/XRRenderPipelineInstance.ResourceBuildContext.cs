using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    /// <summary>
    /// Tracks the generation currently being materialized so resource factories bind into the pending registry.
    /// </summary>
    /// <param name="generation">The render resource generation.</param>
    /// <param name="managedThreadId">The managed thread ID.</param>
    internal sealed class ResourceBuildContext(RenderResourceGeneration generation, int managedThreadId)
    {
        public RenderResourceGeneration Generation { get; } = generation;
        public int ManagedThreadId { get; } = managedThreadId;
        public ResourceGenerationKey Key => Generation.Key;
    }
}
