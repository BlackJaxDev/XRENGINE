namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    /// <summary>
    /// Retires global light-owned shadow groups and their resource leases.
    /// Scene and material residents have their own publication lifetime and are
    /// intentionally left to their owning transition paths.
    /// </summary>
    public void Dispose()
    {
        if (_publishedLightCount == 0)
            return;

        int shadowCount = 0;
        for (int index = 0; index < _publishedLightCount; ++index)
            shadowCount += _publishedLightShadowCounts[index];

        EnsureGlobalResourceTransitionCapacity(0, shadowCount);
        int releaseCursor = 0;
        for (int index = 0; index < _publishedLightCount; ++index)
        {
            int start = _publishedLightShadowStarts[index];
            int count = _publishedLightShadowCounts[index];
            if (count != 0)
                _publishedShadowBindings.AsSpan(start, count).CopyTo(
                    _resourceReleaseBindings.AsSpan(releaseCursor, count));
            releaseCursor += count;
        }

        AdvancedGlobalResourceDatabase resources = Database.Resources;
        string reason = string.Empty;
        if (!resources.Lights.CanApply(0, 0, _publishedLightCount) ||
            !resources.Shadows.CanApply(0, 0, shadowCount) ||
            !_resourcePublisher.TryPreflightTransition(
                ReadOnlySpan<AdvancedGpuResourceBindingSource>.Empty,
                _resourceReleaseBindings.AsSpan(0, releaseCursor), out reason))
        {
            throw new InvalidOperationException(
                string.IsNullOrEmpty(reason)
                    ? "The retained global resources cannot be retired during publisher disposal."
                    : reason);
        }

        for (int index = 0; index < _publishedLightCount; ++index)
            if (!resources.RemoveLight(_publishedLightHandles[index]))
                throw new InvalidOperationException("A retained global light could not be retired during disposal.");
        for (int index = 0; index < _publishedLightCount; ++index)
        {
            int start = _publishedLightShadowStarts[index];
            int count = _publishedLightShadowCounts[index];
            if (count != 0 && !resources.RemoveShadowGroup(_publishedShadowHandles.AsSpan(start, count)))
                throw new InvalidOperationException("A retained shadow group could not be retired during disposal.");
        }

        _resourcePublisher.ApplyPreflightedAcquisitions(
            ReadOnlySpan<AdvancedGpuResourceBindingSource>.Empty,
            Span<AdvancedMaterialTextureBinding>.Empty);
        _resourcePublisher.ApplyPreflightedReleases();
        Array.Clear(_publishedLightSources);
        Array.Clear(_publishedLightHandles);
        Array.Clear(_publishedLightShadowCounts);
        _publishedLightCount = 0;
    }
}
