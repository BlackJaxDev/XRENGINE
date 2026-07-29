namespace XREngine.Rendering;

/// <summary>
/// Allocation-free counters that become consumable only after frame publication.
/// This keeps missing-resource diagnostics out of the render hot path.
/// </summary>
public sealed class AdvancedResourceResidencyDiagnostics
{
    private long _currentTextureFallbacks;
    private long _currentSamplerFallbacks;
    private long _currentStaleTextureReferences;
    private long _currentStaleSamplerReferences;
    private long _delayedTextureFallbacks;
    private long _delayedSamplerFallbacks;
    private long _delayedStaleTextureReferences;
    private long _delayedStaleSamplerReferences;
    private long _delayedFrameId;

    public void RecordTextureFallback(bool staleGeneration)
    {
        Interlocked.Increment(ref _currentTextureFallbacks);
        if (staleGeneration)
            Interlocked.Increment(ref _currentStaleTextureReferences);
    }

    public void RecordSamplerFallback(bool staleGeneration)
    {
        Interlocked.Increment(ref _currentSamplerFallbacks);
        if (staleGeneration)
            Interlocked.Increment(ref _currentStaleSamplerReferences);
    }

    public void PublishFrame(ulong frameId)
    {
        Interlocked.Add(
            ref _delayedTextureFallbacks,
            Interlocked.Exchange(ref _currentTextureFallbacks, 0L));
        Interlocked.Add(
            ref _delayedSamplerFallbacks,
            Interlocked.Exchange(ref _currentSamplerFallbacks, 0L));
        Interlocked.Add(
            ref _delayedStaleTextureReferences,
            Interlocked.Exchange(ref _currentStaleTextureReferences, 0L));
        Interlocked.Add(
            ref _delayedStaleSamplerReferences,
            Interlocked.Exchange(ref _currentStaleSamplerReferences, 0L));
        Interlocked.Exchange(ref _delayedFrameId, unchecked((long)frameId));
    }

    public bool TryConsume(out AdvancedResourceResidencySnapshot snapshot)
    {
        long textureFallbacks = Interlocked.Exchange(ref _delayedTextureFallbacks, 0L);
        long samplerFallbacks = Interlocked.Exchange(ref _delayedSamplerFallbacks, 0L);
        long staleTextures = Interlocked.Exchange(ref _delayedStaleTextureReferences, 0L);
        long staleSamplers = Interlocked.Exchange(ref _delayedStaleSamplerReferences, 0L);
        if ((textureFallbacks | samplerFallbacks | staleTextures | staleSamplers) == 0L)
        {
            snapshot = default;
            return false;
        }

        snapshot = new(
            unchecked((ulong)Volatile.Read(ref _delayedFrameId)),
            checked((ulong)textureFallbacks),
            checked((ulong)samplerFallbacks),
            checked((ulong)staleTextures),
            checked((ulong)staleSamplers));
        return true;
    }
}
