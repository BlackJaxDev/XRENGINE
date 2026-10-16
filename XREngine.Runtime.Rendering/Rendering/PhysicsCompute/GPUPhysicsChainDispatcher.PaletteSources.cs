using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    // Every published renderer reads its current and previous palettes through this view, at its
    // own slice. Each publication writes the buffers of the new output into the view once. The
    // renderers are bound again only when the palette layout, a palette history reset, the
    // external palette source epoch, or the skinning setting changes.
    private readonly SkinPaletteView _skinPaletteView = new();
    // The renderers that the last full publication bound to the view.
    private readonly List<XRMeshRenderer> _paletteViewRenderers = [];
    // The renderers that the current full publication binds. Empty outside a full publication.
    private readonly HashSet<XRMeshRenderer> _paletteViewBindScratch =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private bool _paletteSourcesValid;
    private long _paletteSourcesLayoutVersion;
    private long _paletteSourcesRendererEpoch;
    private bool _paletteSourcesAllowSkinning;
    private bool _paletteSourcesHadHistoryResets;
    // Only the render thread writes this cumulative count; the stage timings report it.
    private long _paletteSourceRebindCount;

    /// <summary>
    /// Publishes the palette buffers of this output to every bound renderer. Binds the renderers
    /// again only when a witness changed since the last full publication: the palette layout
    /// version, the palette history resets of this or the last publication, the external
    /// palette source epoch (a source that another caller cleared or replaced), or the skinning
    /// setting that the bone coverage reads.
    /// </summary>
    private void PublishBatchedGpuDrivenBonePaletteSources()
    {
        if (_gpuDrivenSkinPaletteBuffer is null || _gpuDrivenPreviousSkinPaletteBuffer is null)
            return;

        HashSet<XRMeshRenderer>? historyResets = _publishedOutputPageIndex >= 0
            ? _outputPages[_publishedOutputPageIndex].PaletteHistoryResetRenderers
            : null;
        bool hasHistoryResets = historyResets is { Count: > 0 };
        // Read the epoch before any bind, so that a source change during the binds is seen at
        // the next publication.
        long rendererEpoch = XRMeshRenderer.ExternalSkinPaletteSourceEpoch;
        bool allowSkinning = RuntimeEngine.Rendering.Settings.AllowSkinning;
        if (_paletteSourcesValid &&
            _paletteSourcesLayoutVersion == _paletteLayoutVersion &&
            _paletteSourcesRendererEpoch == rendererEpoch &&
            _paletteSourcesAllowSkinning == allowSkinning &&
            !hasHistoryResets && !_paletteSourcesHadHistoryResets)
        {
            _skinPaletteView.Publish(_gpuDrivenSkinPaletteBuffer, _gpuDrivenPreviousSkinPaletteBuffer);
            return;
        }

        _paletteSourcesValid = RebindPaletteSources(historyResets);
        _paletteSourcesLayoutVersion = _paletteLayoutVersion;
        _paletteSourcesRendererEpoch = rendererEpoch;
        _paletteSourcesAllowSkinning = allowSkinning;
        _paletteSourcesHadHistoryResets = hasHistoryResets;
        ++_paletteSourceRebindCount;
    }

    /// <summary>
    /// Binds each publishable layout entry to the view, after it detaches the renderers that
    /// this publication does not bind. A detached renderer keeps the buffers that it reads now,
    /// as a renderer that this publication skips must. Returns false when an entry was not bound,
    /// so the next publication tries again.
    /// </summary>
    private bool RebindPaletteSources(HashSet<XRMeshRenderer>? historyResets)
    {
        bool allBound = true;
        ReadOnlySpan<PaletteLayoutEntry> entries = CollectionsMarshal.AsSpan(_paletteLayoutEntries);
        lock (_registrationTransitionSync)
        {
            for (int index = 0; index < entries.Length; ++index)
            {
                if (IsPaletteSourcePublishable(in entries[index]))
                    _paletteViewBindScratch.Add(entries[index].Renderer);
                else
                    allBound = false;
            }
            for (int index = 0; index < _paletteViewRenderers.Count; ++index)
                if (!_paletteViewBindScratch.Contains(_paletteViewRenderers[index]))
                    _paletteViewRenderers[index].DetachSkinPaletteView(_skinPaletteView);
            _paletteViewRenderers.Clear();
            _paletteViewBindScratch.Clear();

            _skinPaletteView.Publish(_gpuDrivenSkinPaletteBuffer, _gpuDrivenPreviousSkinPaletteBuffer);
            for (int index = 0; index < entries.Length; ++index)
            {
                ref readonly PaletteLayoutEntry entry = ref entries[index];
                if (!IsPaletteSourcePublishable(in entry))
                    continue;
                bool historyValid = historyResets is null || !historyResets.Contains(entry.Renderer);
                if (!entry.Renderer.BindGpuDrivenSkinPaletteView(entry.Component, _skinPaletteView,
                    entry.Base, entry.Count, entry.BoneGeneration, entry.DrivesCompleteBonePalette, historyValid))
                {
                    XREngine.Debug.LogWarning("A physics-chain palette binding used an old renderer bone set.");
                    allBound = false;
                    continue;
                }
                _paletteViewRenderers.Add(entry.Renderer);
            }
        }
        return allBound;
    }

    /// <summary>True when the entry's request is still the registered request of its component.</summary>
    private bool IsPaletteSourcePublishable(in PaletteLayoutEntry entry)
        => entry.Request is { } captured
            && _registeredComponents.TryGetValue(entry.Component, out GPUPhysicsChainRequest? current)
            && ReferenceEquals(captured, current);

    /// <summary>
    /// Detaches every bound renderer, so that each keeps its last buffers, and makes the next
    /// publication bind again.
    /// </summary>
    private void ResetPaletteSources()
    {
        for (int index = 0; index < _paletteViewRenderers.Count; ++index)
            _paletteViewRenderers[index].DetachSkinPaletteView(_skinPaletteView);
        _paletteViewRenderers.Clear();
        _paletteSourcesValid = false;
    }
}
