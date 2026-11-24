using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Pass edits on a <see cref="RenderPipelineDraftState"/>: scene pass indices, sorters, and explicit dependencies.
/// </summary>
public static class RenderPipelineDraftPassEdits
{
    /// <summary>Adds a scene pass with the next free index.</summary>
    /// <returns>The new pass index.</returns>
    public static int AddScenePass(RenderPipelineDraftState state, IComparer<RenderCommand>? sorter = null)
    {
        int index = 0;
        foreach (int key in state.RenderPasses.Keys)
            index = Math.Max(index, key + 1);

        state.RenderPasses[index] = sorter;
        return index;
    }

    /// <summary>Sets the sorter of a scene pass. The pass must exist.</summary>
    public static bool SetSorter(RenderPipelineDraftState state, int passIndex, IComparer<RenderCommand>? sorter)
    {
        if (!state.RenderPasses.ContainsKey(passIndex))
            return false;

        state.RenderPasses[passIndex] = sorter;
        return true;
    }

    /// <summary>Moves a scene pass to a new index that no other scene pass uses.</summary>
    public static bool ChangeIndex(RenderPipelineDraftState state, int passIndex, int newIndex)
    {
        if (passIndex == newIndex || state.RenderPasses.ContainsKey(newIndex) ||
            !state.RenderPasses.Remove(passIndex, out IComparer<RenderCommand>? sorter))
        {
            return false;
        }

        state.RenderPasses[newIndex] = sorter;
        return true;
    }

    /// <summary>Removes a scene pass and the explicit dependencies that name it.</summary>
    public static bool RemoveScenePass(RenderPipelineDraftState state, int passIndex)
    {
        if (!state.RenderPasses.Remove(passIndex))
            return false;

        state.PassDependencies.RemoveAll(d => d.PassIndex == passIndex || d.DependsOnPassIndex == passIndex);
        return true;
    }

    /// <summary>Adds an explicit dependency. A duplicate or a self edge is not added.</summary>
    public static bool AddDependency(RenderPipelineDraftState state, int passIndex, int dependsOnPassIndex)
    {
        if (passIndex == dependsOnPassIndex || FindDependency(state, passIndex, dependsOnPassIndex) >= 0)
            return false;

        state.PassDependencies.Add(new AuthoredPassDependency { PassIndex = passIndex, DependsOnPassIndex = dependsOnPassIndex });
        return true;
    }

    /// <summary>Removes an explicit dependency.</summary>
    public static bool RemoveDependency(RenderPipelineDraftState state, int passIndex, int dependsOnPassIndex)
    {
        int index = FindDependency(state, passIndex, dependsOnPassIndex);
        if (index < 0)
            return false;

        state.PassDependencies.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Returns true when a resource edge already orders <paramref name="passIndex"/> after
    /// <paramref name="dependsOnPassIndex"/>: the earlier pass writes a resource that the later pass reads or writes.
    /// The Workbench shows a warning, because the explicit edge adds nothing.
    /// </summary>
    public static bool IsCoveredByResourceEdge(IReadOnlyCollection<RenderPassMetadata> passes, int passIndex, int dependsOnPassIndex)
    {
        RenderPassMetadata? later = null;
        RenderPassMetadata? earlier = null;
        foreach (RenderPassMetadata pass in passes)
        {
            if (pass.PassIndex == passIndex)
                later = pass;
            else if (pass.PassIndex == dependsOnPassIndex)
                earlier = pass;
        }

        if (later is null || earlier is null)
            return false;

        foreach (RenderPassResourceUsage write in earlier.ResourceUsages)
        {
            if (write.Access is not (ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite))
                continue;

            foreach (RenderPassResourceUsage use in later.ResourceUsages)
            {
                if (string.Equals(write.ResourceName, use.ResourceName, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static int FindDependency(RenderPipelineDraftState state, int passIndex, int dependsOnPassIndex)
    {
        for (int i = 0; i < state.PassDependencies.Count; i++)
        {
            AuthoredPassDependency dependency = state.PassDependencies[i];
            if (dependency.PassIndex == passIndex && dependency.DependsOnPassIndex == dependsOnPassIndex)
                return i;
        }

        return -1;
    }
}
