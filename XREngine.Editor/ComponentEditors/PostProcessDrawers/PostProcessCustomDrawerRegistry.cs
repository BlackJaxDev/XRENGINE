using System;
using System.Collections.Generic;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Editor.ComponentEditors.PostProcessDrawers;

/// <summary>
/// Resolves editor-specific custom drawers for post-process stages.
/// </summary>
public static class PostProcessCustomDrawerRegistry
{
    private static readonly Dictionary<string, IPostProcessStageCustomDrawer> RegisteredDrawersByStageKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<Type, IPostProcessStageCustomDrawer> BuiltInDrawersByBackingType =
        new Dictionary<Type, IPostProcessStageCustomDrawer>
    {
        [typeof(DepthOfFieldSettings)] = new DepthOfFieldStageDrawer(),
        [typeof(ColorGradingSettings)] = new ColorGradingStageDrawer(),
    };

    /// <summary>
    /// Registers a custom drawer for a specific post-processing stage key.
    /// </summary>
    public static void Register(string stageKey, IPostProcessStageCustomDrawer drawer)
    {
        ArgumentNullException.ThrowIfNull(drawer);
        RegisteredDrawersByStageKey[stageKey] = drawer;
    }

    /// <summary>
    /// Retrieves the custom drawer for a stage descriptor, preferring an explicitly attached drawer
    /// on the descriptor, then a user-registered stage override, and finally a built-in drawer selected by backing type.
    /// </summary>
    public static IPostProcessStageCustomDrawer? GetDrawer(PostProcessStageDescriptor descriptor)
    {
        if (descriptor.CustomDrawer is not null)
            return descriptor.CustomDrawer;

        if (RegisteredDrawersByStageKey.TryGetValue(descriptor.Key, out var drawer))
            return drawer;

        return descriptor.BackingType is not null && BuiltInDrawersByBackingType.TryGetValue(descriptor.BackingType, out drawer)
            ? drawer
            : null;
    }
}
