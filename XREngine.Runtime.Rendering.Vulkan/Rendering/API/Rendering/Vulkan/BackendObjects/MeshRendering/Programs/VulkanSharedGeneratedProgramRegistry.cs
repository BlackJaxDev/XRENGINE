namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Shares generated mesh programs between renderers. Without sharing, every renderer
/// built its own program for the same material and vertex source, so each cache keyed
/// by program (material descriptor sets, binding artifacts, pipeline layouts) missed
/// for nearly every draw. A program is destroyed when its last renderer releases it.
/// </summary>
internal static class VulkanSharedGeneratedProgramRegistry
{
    private static readonly Dictionary<VulkanSharedGeneratedProgramKey, VulkanSharedGeneratedProgram> s_programs = [];
    private static readonly Lock s_sync = new();

    /// <summary>Adds a reference to an existing live program for <paramref name="key"/>.</summary>
    public static bool TryAcquire(
        in VulkanSharedGeneratedProgramKey key,
        out VulkanSharedGeneratedProgram? shared)
    {
        shared = null;
        lock (s_sync)
        {
            if (!s_programs.TryGetValue(key, out VulkanSharedGeneratedProgram? existing) ||
                existing.Data.IsDestroyed)
            {
                return false;
            }

            existing.References++;
            shared = existing;
            return true;
        }
    }

    /// <summary>
    /// Publishes a newly created program with one reference. If another renderer
    /// published the same key first, the new program is destroyed and the existing
    /// one is returned with one more reference.
    /// </summary>
    public static VulkanSharedGeneratedProgram Publish(
        in VulkanSharedGeneratedProgramKey key,
        XRRenderProgram data,
        VkRenderProgram program)
    {
        VulkanSharedGeneratedProgram created = new(key, data, program) { References = 1 };
        VulkanSharedGeneratedProgram? winner = null;
        lock (s_sync)
        {
            if (s_programs.TryGetValue(key, out VulkanSharedGeneratedProgram? existing) &&
                !existing.Data.IsDestroyed)
            {
                existing.References++;
                winner = existing;
            }
            else
            {
                s_programs[key] = created;
            }
        }

        if (winner is null)
            return created;

        data.Destroy();
        return winner;
    }

    /// <summary>Releases one reference and destroys the program after the last one.</summary>
    public static void Release(VulkanSharedGeneratedProgram shared)
    {
        lock (s_sync)
        {
            if (--shared.References > 0)
                return;

            if (s_programs.TryGetValue(shared.Key, out VulkanSharedGeneratedProgram? current) &&
                ReferenceEquals(current, shared))
            {
                s_programs.Remove(shared.Key);
            }
        }

        shared.Data.Destroy();
    }
}
