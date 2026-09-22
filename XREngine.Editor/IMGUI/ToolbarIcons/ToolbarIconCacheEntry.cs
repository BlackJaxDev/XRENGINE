using System.Runtime.CompilerServices;
using XREngine.Rendering;

namespace XREngine.Editor;

internal sealed class ToolbarIconCacheEntry(
    ToolbarIconCacheKey key,
    string[] candidatePaths,
    int requestRevision)
{
    private readonly ConditionalWeakTable<AbstractRenderer, ToolbarIconRendererState> _rendererStates = new();

    public ToolbarIconCacheKey Key { get; } = key;
    public string[] CandidatePaths { get; } = candidatePaths;
    public int RequestRevision { get; } = requestRevision;
    public ToolbarIconPreparationStatus Status { get; set; } = ToolbarIconPreparationStatus.CpuPending;
    public ToolbarIconPreparedPixels? PreparedPixels { get; set; }
    public XRTexture2D? Texture { get; set; }
    public string? FailureReason { get; set; }

    public ToolbarIconRendererState GetOrCreateRendererState(AbstractRenderer renderer)
        => _rendererStates.GetValue(renderer, static _ => new ToolbarIconRendererState());

    public bool TryGetRendererState(AbstractRenderer renderer, out ToolbarIconRendererState state)
        => _rendererStates.TryGetValue(renderer, out state!);
}
