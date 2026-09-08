using System.Collections.Concurrent;
using XREngine.Data.Rendering;

namespace XREngine;

public static partial class RuntimeEngine
{
    private static readonly ConcurrentDictionary<ulong, RenderPipelineOffscreenIntent> s_advancedOffscreenOwners = [];

    /// <summary>Registers a component-owned advanced capture identity before its pipeline is created.</summary>
    internal static void RegisterAdvancedOffscreenOwner(ulong outputId, in RenderPipelineOffscreenIntent intent)
    {
        if (outputId == 0UL)
            throw new ArgumentOutOfRangeException(nameof(outputId));
        if (!s_advancedOffscreenOwners.TryAdd(outputId, intent) &&
            (!s_advancedOffscreenOwners.TryGetValue(outputId, out RenderPipelineOffscreenIntent existing) || existing != intent))
            throw new InvalidOperationException("An advanced offscreen output identity is already registered for a different intent.");
    }

    internal static void UnregisterAdvancedOffscreenOwner(ulong outputId, in RenderPipelineOffscreenIntent intent)
    {
        if (s_advancedOffscreenOwners.TryGetValue(outputId, out RenderPipelineOffscreenIntent existing) && existing == intent)
            _ = s_advancedOffscreenOwners.TryRemove(outputId, out _);
    }

    internal static bool HasAdvancedOffscreenOwner(ulong outputId, in RenderPipelineOffscreenIntent intent)
        => outputId != 0UL && s_advancedOffscreenOwners.TryGetValue(outputId, out RenderPipelineOffscreenIntent existing) && existing == intent;
}
