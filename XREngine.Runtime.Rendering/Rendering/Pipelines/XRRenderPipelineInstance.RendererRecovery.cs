namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    /// <summary>
    /// Discards output-local resources and preparation failures after their renderer
    /// has retired, while retaining the pipeline asset and the viewport's scene commands.
    /// The caller must first release that device and its API wrappers. Ordinary resize
    /// must continue using transactional replacement generations instead.
    /// </summary>
    public void ResetAfterRendererRetirement(AbstractRenderer retiredRenderer)
    {
        ArgumentNullException.ThrowIfNull(retiredRenderer);
        EnsureOwnedMutationRunsOnRenderThread();
        if (retiredRenderer.AcceptsBackendWork || retiredRenderer.RenderObjectCache.Count != 0)
            throw new InvalidOperationException("Renderer recovery requires a retired renderer with no remaining API wrappers.");
        if (_resourceBuildContext is not null)
            throw new InvalidOperationException("Renderer recovery cannot interrupt resource materialization.");

        using AbstractRenderer.ThreadCurrentScope scope = AbstractRenderer.EnterThreadCurrentScope(retiredRenderer);
        DestroyCacheOnRenderThread();
        RemoveDestroyedResourceAliases(Variables.TextureVariables);
        RemoveDestroyedResourceAliases(Variables.FrameBufferVariables);
        RemoveDestroyedResourceAliases(Variables.BufferVariables);
        RemoveDestroyedResourceAliases(Variables.RenderBufferVariables);
        ResetPipelineScopedRuntimeState();
        _requiresManagedResourceGeneration = null;
        _classifiedResourceLayoutKey = null;
        _lastSuccessfulLayoutlessResourceKey = null;
        ResourceGeneration++;
    }

    private static void RemoveDestroyedResourceAliases<T>(Dictionary<string, T> aliases) where T : GenericRenderObject
    {
        // Preserve authored CPU values and external assets. Only aliases to the
        // output-local objects just destroyed by the retired generation are stale.
        foreach (KeyValuePair<string, T> pair in aliases)
            if (pair.Value.IsDestroyed || pair.Value.IsDestroyQueued)
                aliases.Remove(pair.Key);
    }
}
