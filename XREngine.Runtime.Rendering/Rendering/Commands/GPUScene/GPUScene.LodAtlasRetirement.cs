namespace XREngine.Rendering.Commands
{
    public partial class GPUScene
    {
        // Atlas references of LOD levels whose CPU mesh left. The LOD table shows mesh ID 0 for such a level at once, but a
        // frame that the GPU still runs can read the old ID, so the atlas range stays until a frame boundary fence signals.
        private readonly List<PendingLodAtlasRelease> _pendingLodAtlasReleases = [];

        /// <summary>The number of evicted LOD atlas references that wait for the frames in flight to end.</summary>
        internal int PendingLodAtlasReleaseCount
        {
            get
            {
                using (_lock.EnterScope())
                    return _pendingLodAtlasReleases.Count;
            }
        }

        /// <summary>Gets the atlas reference count of a mesh, or zero when the mesh has no atlas range.</summary>
        internal int GetAtlasMeshReferenceCount(XRMesh mesh)
        {
            using (_lock.EnterScope())
                return _atlasMeshRefCounts.TryGetValue(mesh, out int count) ? count : 0;
        }

        /// <summary>
        /// Keeps the atlas reference of an evicted LOD level until the frames in flight end. Call it with the scene lock held,
        /// after the level left the LOD table.
        /// </summary>
        private void DeferLodAtlasRelease(uint meshId, int amount)
        {
            if (meshId == 0 || amount <= 0)
                return;

            _pendingLodAtlasReleases.Add(new PendingLodAtlasRelease(meshId, amount));
        }

        /// <summary>
        /// Releases the atlas references of evicted LOD levels whose frames ended. Runs at each frame boundary
        /// (<see cref="SwapCommandBuffers"/>). A release that waits gets a GPU fence at the first boundary after the eviction,
        /// which covers every frame that could read the old level ID. It is released at a later boundary, when that fence
        /// signaled. Without a renderer fence, the release happens at the second boundary. With no pending release, this
        /// method does nothing and allocates nothing.
        /// </summary>
        internal void RetirePendingLodAtlasReleasesAtFrameBoundary()
        {
            using (_lock.EnterScope())
            {
                if (_pendingLodAtlasReleases.Count == 0)
                    return;

                bool released = false;
                for (int i = 0; i < _pendingLodAtlasReleases.Count;)
                {
                    PendingLodAtlasRelease pending = _pendingLodAtlasReleases[i];
                    if (!pending.FenceIssued)
                    {
                        pending.Fence = AbstractRenderer.Current?.InsertGpuFence();
                        pending.FenceIssued = true;
                        _pendingLodAtlasReleases[i] = pending;
                        i++;
                        continue;
                    }

                    if (pending.Fence is { } fence && fence.Poll() == EGpuFenceStatus.Pending)
                    {
                        i++;
                        continue;
                    }

                    pending.Fence?.Dispose();
                    _pendingLodAtlasReleases.RemoveAt(i);
                    DecrementAtlasMeshRefCount(pending.MeshId, "LodEviction", pending.Amount);
                    released = true;
                }

                if (released)
                    FlushMeshDataDirtyRange();
            }
        }

        /// <summary>Drops every pending LOD atlas release. The scene clears or destroys its atlas, so no range stays.</summary>
        private void DiscardPendingLodAtlasReleases()
        {
            for (int i = 0; i < _pendingLodAtlasReleases.Count; i++)
                _pendingLodAtlasReleases[i].Fence?.Dispose();
            _pendingLodAtlasReleases.Clear();
        }

        /// <summary>One atlas reference of an evicted LOD level and the fence of the frames that can still read it.</summary>
        private struct PendingLodAtlasRelease(uint meshId, int amount)
        {
            public readonly uint MeshId = meshId;
            public readonly int Amount = amount;
            public bool FenceIssued;
            public XRGpuFence? Fence;
        }
    }
}
