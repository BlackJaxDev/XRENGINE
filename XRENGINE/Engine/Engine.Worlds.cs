using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine;

public static partial class Engine
{
    /// <summary>
    /// Resolves the Bootstrap-owned host for a serialized world and returns its
    /// canonical Core runtime context.
    /// </summary>
    public static RuntimeWorld GetOrCreateWorld(XRWorld targetWorld)
    {
        ArgumentNullException.ThrowIfNull(targetWorld);
        IRuntimeWorldHostServices host = RuntimeWorldHostServices.Current
            ?? throw new InvalidOperationException(
                "Runtime world host services are not installed. Install the Bootstrap runtime host before creating windows or worlds.");
        return host.GetOrCreate(targetWorld);
    }

    /// <summary>Returns the Rendering capability composed for a serialized world.</summary>
    public static IRuntimeRenderWorld GetOrCreateRenderWorld(XRWorld targetWorld)
    {
        RuntimeWorld world = GetOrCreateWorld(targetWorld);
        return world.GetRenderWorld()
            ?? throw new InvalidOperationException(
                $"The runtime host did not attach a render world for '{targetWorld.Name ?? "<unnamed>"}'.");
    }
}
