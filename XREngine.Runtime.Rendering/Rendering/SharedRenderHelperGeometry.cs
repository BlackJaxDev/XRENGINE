using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Owns immutable CPU geometry shared by render helpers while leaving materials,
/// renderers, shader variants, and backend wrappers scoped to each consumer.
/// </summary>
internal static class SharedRenderHelperGeometry
{
    private static readonly object SyncRoot = new();
    private static readonly GeometryEntry FullscreenTriangleEntry = new(
        "FullscreenTriangle",
        CreateFullscreenTriangle);
    private static readonly GeometryEntry FullscreenQuadEntry = new(
        "FullscreenQuad",
        CreateFullscreenQuad);

    /// <summary>
    /// Acquires one immutable fullscreen mesh. The returned lease must outlive
    /// every renderer that references its mesh.
    /// </summary>
    internal static Lease AcquireFullscreen(bool useTriangle)
    {
        GeometryEntry entry = useTriangle
            ? FullscreenTriangleEntry
            : FullscreenQuadEntry;

        while (true)
        {
            lock (SyncRoot)
            {
                if (entry.Mesh is not null)
                    return AcquireLiveEntry(entry);

                if (entry.IsCreating)
                {
                    Monitor.Wait(SyncRoot);
                    continue;
                }

                entry.IsCreating = true;
            }

            return CreateAndAcquireEntry(entry);
        }
    }

    private static Lease CreateAndAcquireEntry(GeometryEntry entry)
    {
        XRMesh mesh;
        TimeSpan constructionDuration;
        try
        {
            long startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            mesh = entry.Factory();
            constructionDuration = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp);
            mesh.Name = $"Shared.{entry.Name}";
        }
        catch
        {
            lock (SyncRoot)
            {
                entry.IsCreating = false;
                Monitor.PulseAll(SyncRoot);
            }
            throw;
        }

        lock (SyncRoot)
        {
            entry.Mesh = mesh;
            entry.ConstructionCount++;
            entry.ConstructionDuration += constructionDuration;
            entry.IsCreating = false;
            Monitor.PulseAll(SyncRoot);
            return AcquireLiveEntry(entry);
        }
    }

    private static Lease AcquireLiveEntry(GeometryEntry entry)
    {
        XRMesh mesh = entry.Mesh ??
            throw new InvalidOperationException($"Shared helper geometry '{entry.Name}' was not published.");

        entry.ActiveLeaseCount++;
        entry.TotalAcquireCount++;
        if (entry.ActiveLeaseCount > entry.PeakLeaseCount)
            entry.PeakLeaseCount = entry.ActiveLeaseCount;

        Debug.Rendering(
            "[SharedHelperGeometry] kind={0} action=Acquire active={1} peak={2} total={3} constructed={4} avoided={5} constructionMs={6:F3}",
            entry.Name,
            entry.ActiveLeaseCount,
            entry.PeakLeaseCount,
            entry.TotalAcquireCount,
            entry.ConstructionCount,
            entry.TotalAcquireCount - entry.ConstructionCount,
            entry.ConstructionDuration.TotalMilliseconds);

        return new Lease(entry, mesh);
    }

    private static void Release(GeometryEntry entry, XRMesh mesh)
    {
        bool retireMesh = false;
        lock (SyncRoot)
        {
            if (entry.ActiveLeaseCount <= 0)
                throw new InvalidOperationException($"Shared helper geometry '{entry.Name}' has no active lease to release.");

            entry.ActiveLeaseCount--;
            if (entry.ActiveLeaseCount == 0 && ReferenceEquals(entry.Mesh, mesh))
            {
                entry.Mesh = null;
                retireMesh = true;
            }

            Debug.Rendering(
                "[SharedHelperGeometry] kind={0} action=Release active={1} peak={2} total={3} constructed={4} avoided={5} retiring={6}",
                entry.Name,
                entry.ActiveLeaseCount,
                entry.PeakLeaseCount,
                entry.TotalAcquireCount,
                entry.ConstructionCount,
                entry.TotalAcquireCount - entry.ConstructionCount,
                retireMesh);
        }

        // The consumer retires its XRMeshRenderer before releasing the lease.
        // Logical teardown is immediate; backend wrapper retirement still drains
        // already-frozen native work through the owning renderer.
        if (retireMesh)
            mesh.Destroy(now: true);
    }

    private static XRMesh CreateFullscreenTriangle()
    {
        VertexTriangle triangle = new(
            new Vector3(-1, -1, 0),
            new Vector3( 3, -1, 0),
            new Vector3(-1,  3, 0));
        return XRMesh.Create(triangle);
    }

    private static XRMesh CreateFullscreenQuad()
    {
        VertexTriangle lowerRight = new(
            new Vector3(-1, -1, 0),
            new Vector3( 1, -1, 0),
            new Vector3( 1,  1, 0));
        VertexTriangle upperLeft = new(
            new Vector3(-1, -1, 0),
            new Vector3( 1,  1, 0),
            new Vector3(-1,  1, 0));
        return XRMesh.Create(lowerRight, upperLeft);
    }

    internal sealed class Lease : IDisposable
    {
        private GeometryEntry? _entry;

        internal Lease(GeometryEntry entry, XRMesh mesh)
        {
            _entry = entry;
            Mesh = mesh;
        }

        /// <summary>
        /// Immutable geometry owned by the shared cache. Consumers must not
        /// mutate or destroy it.
        /// </summary>
        internal XRMesh Mesh { get; }

        public void Dispose()
        {
            GeometryEntry? entry = Interlocked.Exchange(ref _entry, null);
            if (entry is not null)
                Release(entry, Mesh);
        }
    }

    internal sealed class GeometryEntry(string name, Func<XRMesh> factory)
    {
        public string Name { get; } = name;
        public Func<XRMesh> Factory { get; } = factory;
        public XRMesh? Mesh { get; set; }
        public int ActiveLeaseCount { get; set; }
        public int PeakLeaseCount { get; set; }
        public long TotalAcquireCount { get; set; }
        public long ConstructionCount { get; set; }
        public TimeSpan ConstructionDuration { get; set; }
        public bool IsCreating { get; set; }
    }
}
