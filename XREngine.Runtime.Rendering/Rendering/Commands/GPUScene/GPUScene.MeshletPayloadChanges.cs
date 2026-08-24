// =====================================================================================
// GPUScene.MeshletPayloadChanges.cs - Resident payload replacement observation.
// =====================================================================================

using System.Collections.Concurrent;
using System.Collections.Generic;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Rendering.Commands
{
    public partial class GPUScene
    {
        // Payload changes can originate from asset reload/import work outside the
        // collect thread. Coalesce them here and rebuild only from the existing
        // SwapCommandBuffers frame-boundary publication point.
        private readonly ConcurrentDictionary<XRMesh, byte> _pendingMeshletPayloadChanges =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<XRMesh> _meshletPayloadSubscriptions =
            new(ReferenceEqualityComparer.Instance);

        private void SubscribeMeshletPayloadChanges(XRMesh mesh)
        {
            if (!_meshletPayloadSubscriptions.Add(mesh))
                return;

            mesh.PropertyChanged += MeshletPayloadPropertyChanged;
        }

        private void UnsubscribeMeshletPayloadChanges(XRMesh mesh)
        {
            if (!_meshletPayloadSubscriptions.Remove(mesh))
                return;

            mesh.PropertyChanged -= MeshletPayloadPropertyChanged;
            _pendingMeshletPayloadChanges.TryRemove(mesh, out _);
        }

        private void UnsubscribeAllMeshletPayloadChanges()
        {
            foreach (XRMesh mesh in _meshletPayloadSubscriptions)
                mesh.PropertyChanged -= MeshletPayloadPropertyChanged;

            _meshletPayloadSubscriptions.Clear();
            _pendingMeshletPayloadChanges.Clear();
        }

        private void MeshletPayloadPropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
        {
            if (sender is XRMesh mesh && args.PropertyName == nameof(XRMesh.MeshletPayload))
                _pendingMeshletPayloadChanges.TryAdd(mesh, 0);
        }

        /// <summary>
        /// Resolves coalesced resident payload replacements immediately before the
        /// coherent meshlet buffer generation is published for the next frame.
        /// </summary>
        private void ApplyPendingMeshletPayloadChangesAtFrameBoundary()
        {
            if (_pendingMeshletPayloadChanges.IsEmpty)
                return;

            foreach (XRMesh mesh in _pendingMeshletPayloadChanges.Keys)
            {
                if (!_pendingMeshletPayloadChanges.TryRemove(mesh, out _))
                    continue;
                if (!_meshIDMap.TryGetValue(mesh, out uint meshId) ||
                    !_atlasMeshRefCounts.ContainsKey(mesh))
                {
                    continue;
                }

                if (_activeAtlasTiers.TryGetValue(mesh, out EAtlasTier tier) &&
                    tier != EAtlasTier.Streaming)
                {
                    EnsureMeshletRangeForMesh(meshId, mesh);
                }
                else
                {
                    SetEmptyMeshletRange(meshId, 0UL);
                }
            }
        }
    }
}
