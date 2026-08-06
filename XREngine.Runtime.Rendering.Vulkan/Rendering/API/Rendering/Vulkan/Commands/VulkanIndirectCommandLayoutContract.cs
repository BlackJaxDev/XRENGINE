using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Verifies that the Silk.NET records supplied to Vulkan indirect draw commands retain
/// the ABI mandated by Vulkan. This runs during renderer initialization, never per draw.
/// </summary>
internal static class VulkanIndirectCommandLayoutContract
{
    private const int DrawIndirectCommandSize = 16;
    private const int DrawIndexedIndirectCommandSize = 20;

    public static void ValidateRuntimeLayout()
    {
        RequireSize<DrawIndirectCommand>(DrawIndirectCommandSize);
        RequireOffset<DrawIndirectCommand>(nameof(DrawIndirectCommand.VertexCount), 0);
        RequireOffset<DrawIndirectCommand>(nameof(DrawIndirectCommand.InstanceCount), 4);
        RequireOffset<DrawIndirectCommand>(nameof(DrawIndirectCommand.FirstVertex), 8);
        RequireOffset<DrawIndirectCommand>(nameof(DrawIndirectCommand.FirstInstance), 12);

        RequireSize<DrawIndexedIndirectCommand>(DrawIndexedIndirectCommandSize);
        RequireOffset<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.IndexCount), 0);
        RequireOffset<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.InstanceCount), 4);
        RequireOffset<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.FirstIndex), 8);
        RequireOffset<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.VertexOffset), 12);
        RequireOffset<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.FirstInstance), 16);
    }

    private static void RequireSize<T>(int expected) where T : unmanaged
    {
        int unsafeSize = Unsafe.SizeOf<T>();
        int marshalSize = Marshal.SizeOf<T>();
        if (unsafeSize != expected || marshalSize != expected)
            throw new InvalidOperationException($"{typeof(T).Name} Vulkan ABI size mismatch: Unsafe.SizeOf={unsafeSize}, Marshal.SizeOf={marshalSize}, expected={expected}.");
    }

    private static void RequireOffset<T>(string fieldName, int expected) where T : unmanaged
    {
        int actual = checked((int)Marshal.OffsetOf<T>(fieldName));
        if (actual != expected)
            throw new InvalidOperationException($"{typeof(T).Name}.{fieldName} Vulkan ABI offset mismatch: actual={actual}, expected={expected}.");
    }
}
