using System.Runtime.CompilerServices;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Tracks the render assets (render objects, and meshes with the buffers they own) the latest
/// snapshot restore of each scene created. A restore replaces a scene's content wholesale, so
/// the next restore of the same scene destroys what the previous one created, together with
/// the roots it replaces: nothing else owns them, and until destroyed they keep their renderer
/// resources registered. Content no restore created (the world as loaded before the first play
/// entry) can share objects with other owners, such as import caches and engine defaults, and
/// is left alone.
/// </summary>
internal static class SnapshotRestoredContent
{
    private static readonly ConditionalWeakTable<XRScene, List<XRAsset>> CreatedRenderAssets = new();

    /// <summary>
    /// Records the render assets a restore created for <paramref name="scene"/>, first
    /// destroying the replaced roots and the render assets of the previous restore when a
    /// restore created the content being replaced.
    /// </summary>
    public static void Replace(XRScene scene, IReadOnlyList<SceneNode> replacedRoots, List<XRAsset> createdRenderAssets)
    {
        if (CreatedRenderAssets.TryGetValue(scene, out List<XRAsset>? previous))
        {
            int failures = 0;
            foreach (SceneNode root in replacedRoots)
                failures += TryDestroy(root);

            // Reverse creation order: the reader completes an asset after the assets it holds.
            for (int index = previous.Count - 1; index >= 0; index--)
                failures += TryDestroy(previous[index]);

            SnapshotDiagnostics.Log(
                $"Released the previous restore of scene '{scene.Name ?? "<unnamed>"}': roots={replacedRoots.Count} renderAssets={previous.Count} failures={failures}");
        }

        CreatedRenderAssets.AddOrUpdate(scene, createdRenderAssets);
    }

    /// <summary>Destroys the render assets a failed restore created before it stopped.</summary>
    public static void Discard(List<XRAsset> createdRenderAssets)
    {
        for (int index = createdRenderAssets.Count - 1; index >= 0; index--)
            TryDestroy(createdRenderAssets[index]);
    }

    private static int TryDestroy(XRObjectBase value)
    {
        try
        {
            value.Destroy(now: true);
            return 0;
        }
        catch (Exception ex)
        {
            SnapshotDiagnostics.Warning($"Destroying {value.GetType().Name} '{value.Name ?? "<unnamed>"}' from a replaced restore failed: {ex}");
            return 1;
        }
    }
}
