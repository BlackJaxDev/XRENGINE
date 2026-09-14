namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free transferable receipt for one native realization use. It is
/// always carried beside the canonical publication lease and shares that
/// lease's exact frame-slot completion boundary.
/// </summary>
internal readonly struct VulkanAdvancedScenePublicationUse : IDisposable
{
    private readonly VulkanAdvancedScenePublicationUseState? _state;
    private readonly uint _generation;

    internal VulkanAdvancedScenePublicationUse(
        VulkanAdvancedScenePublicationUseState state,
        uint generation,
        in VulkanAdvancedScenePublicationState publicationState)
    {
        _state = state;
        _generation = generation;
        PublicationState = publicationState;
    }

    internal VulkanAdvancedScenePublicationState PublicationState { get; }

    /// <summary>Distinguishes an owned receipt, including stale copies, from an empty raw-lease row.</summary>
    internal bool HasReceipt => _state is not null;

    internal ulong NativeGeneration
        => IsValid ? PublicationState.NativeGeneration : 0u;

    internal bool IsValid
        => PublicationState.IsValid && _state?.IsCurrent(_generation) == true;

    public void Dispose()
        => _state?.Release(_generation);
}
