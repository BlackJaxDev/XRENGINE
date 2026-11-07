using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Overlays;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Edits the overlay chain of a derived pipeline: revert one patch, and accept a rebase after the base changed. Each
/// edit returns a new overlay list, so the caller sets <see cref="RenderPipeline.Overlays"/> once.
/// </summary>
public static class RenderPipelineOverlayEdits
{
    /// <summary>Returns a copy of <paramref name="overlays"/> without <paramref name="patch"/>.</summary>
    /// <param name="overlays">The overlay chain, root overlay first.</param>
    /// <param name="overlayIndex">The index of the overlay that holds the patch.</param>
    /// <param name="patch">The patch object from the overlay or from a patch result.</param>
    /// <returns>The new chain, or null when the overlay does not hold the patch.</returns>
    public static IReadOnlyList<RenderPipelineOverlay>? RemovePatch(IReadOnlyList<RenderPipelineOverlay> overlays, int overlayIndex, object patch)
    {
        if ((uint)overlayIndex >= (uint)overlays.Count)
            return null;

        RenderPipelineOverlay source = overlays[overlayIndex];
        RenderPipelineOverlay copy = source.Clone();
        bool removed = patch switch
        {
            RenderCommandPatch command => RemoveAt(copy.CommandPatches, source.CommandPatches.FindIndex(p => ReferenceEquals(p, command))),
            RenderResourcePatch resource => RemoveAt(copy.ResourcePatches, source.ResourcePatches.FindIndex(p => ReferenceEquals(p, resource))),
            PostProcessSchemaPatch schema => RemoveAt(copy.SchemaPatches, source.SchemaPatches.FindIndex(p => ReferenceEquals(p, schema))),
            _ => false,
        };

        return removed ? Replace(overlays, overlayIndex, copy) : null;
    }

    /// <summary>
    /// Returns a copy of the overlay chain of <paramref name="pipeline"/> in which the overlay at
    /// <paramref name="overlayIndex"/> accepts the current base: each command target that the base renamed takes the
    /// new anchor id, and the saved base hash becomes the hash of the current base.
    /// </summary>
    public static IReadOnlyList<RenderPipelineOverlay>? AcceptRebase(RenderPipeline pipeline, int overlayIndex)
    {
        IReadOnlyList<RenderPipelineOverlay> overlays = pipeline.Overlays;
        if ((uint)overlayIndex >= (uint)overlays.Count)
            return null;

        RenderPipelineOverlay copy = overlays[overlayIndex].Clone();
        for (int i = 0; i < copy.CommandPatches.Count; i++)
        {
            RenderCommandPatch patch = copy.CommandPatches[i];
            patch.Target = ResolveAlias(pipeline.AnchorAliases, patch.Target);
        }

        copy.BaseStructureHash = pipeline.ComputeBaseStructureHash(overlayIndex);
        return Replace(overlays, overlayIndex, copy);
    }

    /// <summary>Gets whether the overlay at <paramref name="overlayIndex"/> has a saved base hash that differs from the current base.</summary>
    public static bool NeedsRebase(RenderPipeline pipeline, int overlayIndex)
    {
        IReadOnlyList<RenderPipelineOverlay> overlays = pipeline.Overlays;
        if ((uint)overlayIndex >= (uint)overlays.Count || overlays[overlayIndex].BaseStructureHash is not { } saved)
            return false;

        return saved != pipeline.ComputeBaseStructureHash(overlayIndex);
    }

    /// <summary>Follows the anchor renames of the base. A cycle stops at the first repeated id.</summary>
    private static string ResolveAlias(Dictionary<string, string> aliases, string target)
    {
        string current = target;
        for (int hops = 0; hops < aliases.Count && aliases.TryGetValue(current, out string? next); hops++)
            current = next;
        return current;
    }

    private static bool RemoveAt<T>(List<T> list, int index)
    {
        if ((uint)index >= (uint)list.Count)
            return false;

        list.RemoveAt(index);
        return true;
    }

    private static RenderPipelineOverlay[] Replace(IReadOnlyList<RenderPipelineOverlay> overlays, int index, RenderPipelineOverlay overlay)
    {
        RenderPipelineOverlay[] result = new RenderPipelineOverlay[overlays.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = i == index ? overlay : overlays[i];
        return result;
    }
}
