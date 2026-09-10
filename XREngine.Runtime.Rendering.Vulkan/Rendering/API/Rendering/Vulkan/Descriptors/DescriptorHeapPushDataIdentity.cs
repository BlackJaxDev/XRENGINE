namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact, allocation-free identity of program-owned descriptor-heap push data.
/// The payload remains mutable only through its owner; a changed payload advances
/// its content generation before it can authorize another recorded command.
/// </summary>
internal readonly record struct DescriptorHeapPushDataIdentity(
    ulong OwnerIdentity,
    ulong ContentGeneration,
    ulong LayoutIdentity,
    uint ShaderConstantByteCount,
    uint PushByteCount)
{
    internal static readonly DescriptorHeapPushDataIdentity CompleteNotUsed =
        new(1UL, 1UL, 1UL, 0U, 0U);

    internal bool IsComplete
        => OwnerIdentity != 0UL && ContentGeneration != 0UL && LayoutIdentity != 0UL &&
           PushByteCount >= ShaderConstantByteCount;
}
