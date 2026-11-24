namespace XREngine.Rendering;

/// <summary>
/// Releases retained editor picking sources that fall too far behind the current scene
/// publication, also for a pipeline that no longer renders.
/// </summary>
/// <remarks>
/// A retained picking source keeps a GPU lease on its scene publication, and the canonical
/// scene tables cannot trim their journals past a pinned publication. A pipeline replaces its
/// source on each submitted frame, but a pipeline that stops (for example an OpenXR eye whose
/// frames wait on a resource) does not. The GPU scene calls <see cref="Sweep"/> after each world
/// swap, so the bound applies while such a pipeline is stopped. A pipeline registers itself the
/// first time it retains a source. The registry holds weak references, so it does not keep a
/// destroyed pipeline alive. A sweep does not allocate.
/// </remarks>
internal static class EditorPickingSourceSweep
{
    private static readonly object s_sync = new();
    private static readonly List<WeakReference<XRRenderPipelineInstance>> s_pipelines = [];

    /// <summary>Adds <paramref name="pipeline"/> to the sweep. The pipeline calls this once.</summary>
    public static void Register(XRRenderPipelineInstance pipeline)
    {
        lock (s_sync)
            s_pipelines.Add(new WeakReference<XRRenderPipelineInstance>(pipeline));
    }

    /// <summary>
    /// Asks every registered pipeline to release a retained picking source of the scene
    /// database <paramref name="databaseEpoch"/> whose publication is too old for
    /// <paramref name="currentPublicationSequence"/>, and removes the entries of collected
    /// pipelines. Sources of other scene databases (other worlds) are not compared.
    /// </summary>
    public static void Sweep(ulong databaseEpoch, ulong currentPublicationSequence)
    {
        if (currentPublicationSequence == 0UL)
            return;

        lock (s_sync)
        {
            for (int index = s_pipelines.Count - 1; index >= 0; index--)
            {
                if (s_pipelines[index].TryGetTarget(out XRRenderPipelineInstance? pipeline))
                    pipeline.ReleaseStaleEditorPickingSource(databaseEpoch, currentPublicationSequence);
                else
                    s_pipelines.RemoveAt(index);
            }
        }
    }
}
