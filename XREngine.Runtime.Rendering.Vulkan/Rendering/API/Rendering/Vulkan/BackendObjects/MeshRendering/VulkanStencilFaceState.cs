using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stores one Vulkan <see cref="StencilOpState"/> with value equality.
/// <see cref="StencilOpState"/> does not implement <see cref="IEquatable{T}"/>,
/// so the generated equality of a record that holds it boxes the value. Frame
/// signatures hash every draw record on every frame, so the boxing allocated for
/// each draw.
/// </summary>
internal readonly struct VulkanStencilFaceState(StencilOpState state) : IEquatable<VulkanStencilFaceState>
{
    public StencilOpState State { get; } = state;

    public static implicit operator VulkanStencilFaceState(StencilOpState state) => new(state);

    public static implicit operator StencilOpState(VulkanStencilFaceState state) => state.State;

    public bool Equals(VulkanStencilFaceState other)
        => State.FailOp == other.State.FailOp &&
           State.PassOp == other.State.PassOp &&
           State.DepthFailOp == other.State.DepthFailOp &&
           State.CompareOp == other.State.CompareOp &&
           State.CompareMask == other.State.CompareMask &&
           State.WriteMask == other.State.WriteMask &&
           State.Reference == other.State.Reference;

    public override bool Equals(object? obj)
        => obj is VulkanStencilFaceState other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            State.FailOp,
            State.PassOp,
            State.DepthFailOp,
            State.CompareOp,
            State.CompareMask,
            State.WriteMask,
            State.Reference);

    public static bool operator ==(VulkanStencilFaceState left, VulkanStencilFaceState right) => left.Equals(right);

    public static bool operator !=(VulkanStencilFaceState left, VulkanStencilFaceState right) => !left.Equals(right);
}
