using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Diagnostics only: maps submission kind names (the submission diagnostic kind or a retirement
/// marker operation) to small ids, so <see cref="VulkanLifetimeSubmission"/> stays an unmanaged
/// value that existing code can place on the stack. A name is stored once; later lookups do not
/// allocate. Id 0 means unknown, and names past the capacity also map to 0.
/// </summary>
internal static class VulkanLifetimeSubmissionKinds
{
    private const int Capacity = 1024;
    private static readonly object s_sync = new();
    private static readonly Dictionary<string, ushort> s_ids = new(64);
    private static readonly List<string> s_names = new(64) { "unknown" };

    /// <summary>Returns the id of <paramref name="kind"/>, adding it on first use.</summary>
    internal static ushort GetId(string? kind)
    {
        if (string.IsNullOrEmpty(kind))
            return 0;
        lock (s_sync)
        {
            if (s_ids.TryGetValue(kind, out ushort id))
                return id;
            if (s_names.Count >= Capacity)
                return 0;
            id = (ushort)s_names.Count;
            s_names.Add(kind);
            s_ids.Add(kind, id);
            return id;
        }
    }

    /// <summary>Returns the name of <paramref name="id"/>, or "unknown".</summary>
    internal static string GetName(ushort id)
    {
        lock (s_sync)
            return id < s_names.Count ? s_names[id] : s_names[0];
    }
}
