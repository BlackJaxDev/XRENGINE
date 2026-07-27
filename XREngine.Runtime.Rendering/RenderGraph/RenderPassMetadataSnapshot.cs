using System.Collections;

namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Stable pass ordering plus a published mutation generation. The pass objects
/// remain editable through retained builders, while consumers can validate an
/// unchanged snapshot without walking every pass on each draw.
/// </summary>
public sealed class RenderPassMetadataSnapshot : IReadOnlyList<RenderPassMetadata>
{
    private readonly RenderPassMetadata[] _passes;
    private readonly RenderPassMetadataRevisionSource _revisionSource;

    internal RenderPassMetadataSnapshot(
        RenderPassMetadata[] passes,
        RenderPassMetadataRevisionSource revisionSource)
    {
        _passes = passes;
        _revisionSource = revisionSource;
    }

    public int Count => _passes.Length;

    public RenderPassMetadata this[int index] => _passes[index];

    public int RevisionStamp => _revisionSource.Generation;

    public IEnumerator<RenderPassMetadata> GetEnumerator()
        => ((IEnumerable<RenderPassMetadata>)_passes).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => _passes.GetEnumerator();
}
