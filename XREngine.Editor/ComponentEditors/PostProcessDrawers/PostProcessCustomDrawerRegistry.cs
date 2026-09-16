using System;
using System.Collections.Generic;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Editor.ComponentEditors.PostProcessDrawers;

/// <summary>
/// Registry mapping post-process stage keys to editor-specific custom drawers.
/// </summary>
public static class PostProcessCustomDrawerRegistry
{
    private static readonly Dictionary<string, IPostProcessStageCustomDrawer> DrawersByStageKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["depth-of-field"] = new DepthOfFieldStageDrawer(),
        ["color-grading"] = new ColorGradingStageDrawer(),
    };

    /// <summary>
    /// Registers a custom drawer for a specific post-processing stage key.
    /// </summary>
    public static void Register(string stageKey, IPostProcessStageCustomDrawer drawer)
    {
        ArgumentNullException.ThrowIfNull(drawer);
        DrawersByStageKey[stageKey] = drawer;
    }

    /// <summary>
    /// Retrieves the custom drawer for a stage descriptor, preferring an explicitly attached drawer
    /// on the descriptor, or falling back to the registered editor drawer.
    /// </summary>
    public static IPostProcessStageCustomDrawer? GetDrawer(PostProcessStageDescriptor descriptor)
    {
        if (descriptor.CustomDrawer is not null)
            return descriptor.CustomDrawer;

        return DrawersByStageKey.TryGetValue(descriptor.Key, out var drawer) ? drawer : null;
    }
}
