namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Publishes one generation for every mutation made through a related render-pass
/// metadata builder. Built snapshots share this source so revision validation is
/// O(1) without preventing supported post-build builder edits.
/// </summary>
internal sealed class RenderPassMetadataRevisionSource
{
    private int _generation;

    internal int Generation => Volatile.Read(ref _generation);

    internal void Advance()
        => Interlocked.Increment(ref _generation);
}
