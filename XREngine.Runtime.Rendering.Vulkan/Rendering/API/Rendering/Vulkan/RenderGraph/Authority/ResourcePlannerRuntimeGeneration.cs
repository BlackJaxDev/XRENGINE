namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable publication envelope for one coherent resource-planner runtime generation.
/// </summary>
internal sealed class ResourcePlannerRuntimeGeneration
{
    private readonly ResourcePlannerRuntimeState _state;
    private readonly FrameOpContext _activeFrameOpContext;

    public ResourcePlannerRuntimeGeneration(ResourcePlannerRuntimeState state)
    {
        _state = state;
        if (state.LastActiveFrameOpContext is not { } context)
            return;

        _activeFrameOpContext = context;
        HasActiveFrameOpContext = true;
        DescriptorViewFamilyIdentity = HashCode.Combine(
            (int)context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity);
    }

    public ref readonly ResourcePlannerRuntimeState State => ref _state;
    public bool HasActiveFrameOpContext { get; }
    public ref readonly FrameOpContext ActiveFrameOpContext
        => ref _activeFrameOpContext;

    /// <summary>
    /// Stable descriptor-family identity derived once when this planner generation is published.
    /// Keeping this scalar in the immutable envelope avoids copying the large
    /// <see cref="FrameOpContext"/> value for every mesh draw.
    /// </summary>
    public int DescriptorViewFamilyIdentity { get; }
}
