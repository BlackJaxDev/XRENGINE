namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Preallocated shared state that makes copied publication-use values release
/// their native slot ownership at most once.
/// </summary>
internal sealed class VulkanAdvancedScenePublicationUseState
{
    private VulkanAdvancedSceneResourceRuntime? _owner;
    private int _frameSlot;
    private int _entryIndex;
    private uint _generation;
    private int _active;

    internal VulkanAdvancedScenePublicationUse Arm(
        VulkanAdvancedSceneResourceRuntime owner,
        int frameSlot,
        int entryIndex,
        in VulkanAdvancedScenePublicationState publicationState)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (Volatile.Read(ref _active) != 0)
            throw new InvalidOperationException(
                "An active Vulkan advanced-scene publication use cannot be rearmed.");

        _generation = _generation == uint.MaxValue ? 1u : _generation + 1u;
        _owner = owner;
        _frameSlot = frameSlot;
        _entryIndex = entryIndex;
        Volatile.Write(ref _active, 1);
        return new VulkanAdvancedScenePublicationUse(
            this,
            _generation,
            publicationState);
    }

    internal bool IsCurrent(uint generation)
        => generation != 0u && generation == Volatile.Read(ref _generation) &&
           Volatile.Read(ref _active) != 0;

    internal void Release(uint generation)
    {
        if (generation == 0u || generation != Volatile.Read(ref _generation) ||
            Interlocked.Exchange(ref _active, 0) == 0)
        {
            return;
        }

        VulkanAdvancedSceneResourceRuntime? owner = _owner;
        int frameSlot = _frameSlot;
        int entryIndex = _entryIndex;
        _owner = null;
        _frameSlot = -1;
        _entryIndex = -1;
        owner?.ReleaseUse(frameSlot, entryIndex);
    }
}
