using System.Runtime.InteropServices;
using GPUColliderData = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUColliderData;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Dense copy of every request's accepted colliders at its arena offset, with one version for
/// the whole catalog. Each rotating input page records the version and the buffer that it holds,
/// so a page receives one full write only when it is older than the catalog, not one write for
/// each request in each frame.
/// </summary>
/// <remarks>
/// The render thread owns the catalog. The version increases on every row copy, on a size
/// change, and on <see cref="Invalidate"/>, and it never repeats.
/// </remarks>
internal sealed class PhysicsChainColliderInputCatalog
{
    private readonly List<GPUColliderData> _rows = [];

    /// <summary>The catalog version. A page that holds another version needs a full write.</summary>
    public ulong Version { get; private set; } = 1UL;

    /// <summary>All catalog rows, in arena order.</summary>
    public ReadOnlySpan<GPUColliderData> Rows => CollectionsMarshal.AsSpan(_rows);

    /// <summary>Sets the row count to the collider arena high water. A change advances the version.</summary>
    public void Resize(int rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        if (_rows.Count == rowCount)
            return;
        CollectionsMarshal.SetCount(_rows, rowCount);
        ++Version;
    }

    /// <summary>
    /// Copies one request's colliders into its arena slice when <paramref name="stamp"/> differs
    /// from the current offset, arena generation, and signature. Returns false when the slice is
    /// outside the catalog; the stamp and the version then stay unchanged.
    /// </summary>
    public bool TryStage(ReadOnlySpan<GPUColliderData> colliders, int offset, int arenaGeneration,
        int signature, ref PhysicsChainColliderCatalogStamp stamp)
    {
        if (colliders.IsEmpty)
            return true;
        PhysicsChainColliderCatalogStamp current = new(offset, arenaGeneration, signature, Valid: true);
        if (stamp == current)
            return true;
        if (offset < 0 || (long)offset + colliders.Length > _rows.Count)
            return false;
        colliders.CopyTo(CollectionsMarshal.AsSpan(_rows)[offset..]);
        stamp = current;
        ++Version;
        return true;
    }

    /// <summary>Makes every page older than the catalog, so each page receives a full write.</summary>
    public void Invalidate() => ++Version;

    /// <summary>Returns true when <paramref name="page"/> does not hold this version in <paramref name="buffer"/>.</summary>
    public bool PageNeedsWrite(PhysicsChainInputPage page, XRDataBuffer<GPUColliderData>? buffer)
        => _rows.Count > 0 &&
            (page.ColliderCatalogVersion != Version || !ReferenceEquals(page.ColliderCatalogBuffer, buffer));

    /// <summary>Records that <paramref name="buffer"/> of <paramref name="page"/> now holds this version.</summary>
    public void MarkPageWritten(PhysicsChainInputPage page, XRDataBuffer<GPUColliderData>? buffer)
    {
        page.ColliderCatalogVersion = Version;
        page.ColliderCatalogBuffer = buffer;
    }
}
