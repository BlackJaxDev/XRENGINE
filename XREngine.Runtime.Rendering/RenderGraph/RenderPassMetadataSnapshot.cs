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

    /// <summary>
    /// Looks up pass metadata by its stable pass index without walking the
    /// complete snapshot. Snapshots are published in ascending pass-index order.
    /// </summary>
    public bool TryGetPass(int passIndex, out RenderPassMetadata pass)
    {
        int low = 0;
        int high = _passes.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            RenderPassMetadata candidate = _passes[middle];
            if (candidate.PassIndex == passIndex)
            {
                pass = candidate;
                return true;
            }

            if (candidate.PassIndex < passIndex)
                low = middle + 1;
            else
                high = middle - 1;
        }

        pass = null!;
        return false;
    }

    public IEnumerator<RenderPassMetadata> GetEnumerator()
        => ((IEnumerable<RenderPassMetadata>)_passes).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => _passes.GetEnumerator();
}
