// =====================================================================================
// GPUScene.MeshletBufferGenerations.cs - Frame-boundary publication and retirement.
// =====================================================================================

using System;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Extensions;
using XREngine.Rendering;

namespace XREngine.Rendering.Commands
{
    public partial class GPUScene
    {
        // Meshlet buffers are a single descriptor-table generation.  They must never be
        // resized in place: an in-flight task shader can still dereference every one of
        // these four bindings after the update thread has accepted a reimport/unload.
        private const int MaxRetiredMeshletBufferGenerations = 4;
        private readonly Queue<RetiredMeshletBufferGeneration> _retiredMeshletBufferGenerations = [];
        private bool _meshletBufferGenerationDirty = true;
        private ulong _meshletBufferGeneration;
        private ulong _meshletBufferGenerationRebuildCount;
        private ulong _meshletBufferGenerationRetireCount;

        public ulong MeshletBufferGeneration => _meshletBufferGeneration;
        /// <summary>
        /// True only when the active range/descriptor table is the latest coherent
        /// structural snapshot.  Direct meshlet routing must not bind a stale
        /// generation while retirement back-pressure has deferred publication.
        /// </summary>
        public bool IsMeshletBufferGenerationReady =>
            !_meshletBufferGenerationDirty && _meshletBufferGeneration != 0UL;
        public int RetiredMeshletBufferGenerationCount => _retiredMeshletBufferGenerations.Count;
        public ulong RetiredMeshletBufferBytes => GetRetiredMeshletBufferBytes();

        /// <summary>
        /// Runs from <see cref="SwapCommandBuffers"/>, which VisualScene invokes after
        /// collect/update and before the next GPU-pass submission.  The fence inserted
        /// here therefore covers the previous frame's use of the active generation;
        /// the replacement becomes visible before the next command list binds it.
        /// </summary>
        private void PublishMeshletBufferGenerationAtFrameBoundary()
        {
            DrainRetiredMeshletBufferGenerations(force: false);
            if (!_meshletBufferGenerationDirty)
                return;

            // Bounded retirement is intentional.  Deferring a structural publish is
            // safe (the old coherent snapshot continues rendering); destroying a live
            // descriptor table is not.
            bool hasActiveGeneration = _meshletRangeBuffer is not null ||
                _meshletDescriptorBuffer is not null ||
                _meshletVertexIndexBuffer is not null ||
                _meshletTriangleIndexBuffer is not null;
            if (hasActiveGeneration && _retiredMeshletBufferGenerations.Count >= MaxRetiredMeshletBufferGenerations)
                return;

            BuildDenseMeshletStagingSnapshot(
                out List<GpuMeshletDescriptor> descriptors,
                out List<uint> vertexIndices,
                out List<byte> triangleIndices);

            XRDataBuffer rangeBuffer = CreateMeshletRangeBufferSnapshot();
            XRDataBuffer descriptorBuffer = CreateMeshletDescriptorBufferSnapshot(descriptors);
            XRDataBuffer vertexIndexBuffer = CreateMeshletVertexIndexBufferSnapshot(vertexIndices);
            XRDataBuffer triangleIndexBuffer = CreateMeshletTriangleIndexBufferSnapshot(triangleIndices);

            XRDataBuffer? oldRangeBuffer = _meshletRangeBuffer;
            XRDataBuffer? oldDescriptorBuffer = _meshletDescriptorBuffer;
            XRDataBuffer? oldVertexIndexBuffer = _meshletVertexIndexBuffer;
            XRDataBuffer? oldTriangleIndexBuffer = _meshletTriangleIndexBuffer;
            _meshletRangeBuffer = rangeBuffer;
            _meshletDescriptorBuffer = descriptorBuffer;
            _meshletVertexIndexBuffer = vertexIndexBuffer;
            _meshletTriangleIndexBuffer = triangleIndexBuffer;
            _meshletRangeDirtyRange.Clear();
            _meshletBufferGenerationDirty = false;
            ++_meshletBufferGeneration;
            ++_meshletBufferGenerationRebuildCount;

            if (hasActiveGeneration)
            {
                _retiredMeshletBufferGenerations.Enqueue(new RetiredMeshletBufferGeneration(
                    oldRangeBuffer,
                    oldDescriptorBuffer,
                    oldVertexIndexBuffer,
                    oldTriangleIndexBuffer,
                    AbstractRenderer.Current?.InsertGpuFence()));
            }

            RecordMeshletBufferGenerationTelemetry(rebuilds: 1UL, retires: 0UL);
        }

        private void BuildDenseMeshletStagingSnapshot(
            out List<GpuMeshletDescriptor> descriptors,
            out List<uint> vertexIndices,
            out List<byte> triangleIndices)
        {
            descriptors = new List<GpuMeshletDescriptor>(_meshletDescriptors.Count);
            vertexIndices = new List<uint>(_meshletVertexIndices.Count);
            triangleIndices = new List<byte>(_meshletTriangleIndices.Count);

            // A dictionary enumeration is fine here: this is an infrequent structural
            // rebuild at a frame boundary, never a submission-path scan.
            // Copy keys because values are rewritten with their compacted offsets.
            // This allocation is structural-only and keeps the dictionary iteration
            // contract independent of runtime implementation details.
            List<uint> meshIds = [.. _meshletRangesByMeshId.Keys];
            foreach (uint meshId in meshIds)
            {
                GpuMeshletRange oldRange = _meshletRangesByMeshId[meshId];
                if (!oldRange.HasMeshlets)
                    continue;

                if (!TryCopyDenseMeshletRange(oldRange, descriptors, vertexIndices, triangleIndices, out GpuMeshletRange replacement))
                {
                    _meshletRangesByMeshId[meshId] = default;
                    _meshletIneligibleResidentMeshIds.TryAdd(meshId, 0);
                    continue;
                }

                _meshletRangesByMeshId[meshId] = replacement;
                _meshletIneligibleResidentMeshIds.TryRemove(meshId, out _);
            }

            _meshletDescriptors.Clear();
            _meshletDescriptors.AddRange(descriptors);
            _meshletVertexIndices.Clear();
            _meshletVertexIndices.AddRange(vertexIndices);
            _meshletTriangleIndices.Clear();
            _meshletTriangleIndices.AddRange(triangleIndices);
        }

        private bool TryCopyDenseMeshletRange(
            in GpuMeshletRange sourceRange,
            List<GpuMeshletDescriptor> descriptors,
            List<uint> vertexIndices,
            List<byte> triangleIndices,
            out GpuMeshletRange replacement)
        {
            replacement = default;
            ulong descriptorEnd = (ulong)sourceRange.MeshletOffset + sourceRange.MeshletCount;
            if (descriptorEnd > (uint)_meshletDescriptors.Count)
                return false;

            uint sourceVertexEnd = sourceRange.VertexIndexOffset;
            uint sourceTriangleEnd = sourceRange.TriangleIndexOffset;
            for (uint i = 0; i < sourceRange.MeshletCount; ++i)
            {
                GpuMeshletDescriptor descriptor = _meshletDescriptors[(int)(sourceRange.MeshletOffset + i)];
                ulong vertexEnd = (ulong)descriptor.VertexOffset + descriptor.VertexCount;
                ulong triangleEnd = (ulong)descriptor.TriangleByteOffset + (ulong)descriptor.TriangleCount * 3UL;
                if (vertexEnd > (uint)_meshletVertexIndices.Count || triangleEnd > (uint)_meshletTriangleIndices.Count ||
                    descriptor.VertexOffset < sourceRange.VertexIndexOffset || descriptor.TriangleByteOffset < sourceRange.TriangleIndexOffset)
                {
                    return false;
                }

                sourceVertexEnd = Math.Max(sourceVertexEnd, (uint)vertexEnd);
                sourceTriangleEnd = Math.Max(sourceTriangleEnd, (uint)triangleEnd);
            }

            uint replacementMeshletOffset = (uint)descriptors.Count;
            uint replacementVertexOffset = (uint)vertexIndices.Count;
            uint replacementTriangleOffset = (uint)triangleIndices.Count;
            for (uint i = sourceRange.VertexIndexOffset; i < sourceVertexEnd; ++i)
                vertexIndices.Add(_meshletVertexIndices[(int)i]);
            for (uint i = sourceRange.TriangleIndexOffset; i < sourceTriangleEnd; ++i)
                triangleIndices.Add(_meshletTriangleIndices[(int)i]);

            for (uint i = 0; i < sourceRange.MeshletCount; ++i)
            {
                GpuMeshletDescriptor descriptor = _meshletDescriptors[(int)(sourceRange.MeshletOffset + i)];
                descriptor.VertexOffset = replacementVertexOffset + descriptor.VertexOffset - sourceRange.VertexIndexOffset;
                descriptor.TriangleByteOffset = replacementTriangleOffset + descriptor.TriangleByteOffset - sourceRange.TriangleIndexOffset;
                descriptors.Add(descriptor);
            }

            replacement = new GpuMeshletRange
            {
                MeshletOffset = replacementMeshletOffset,
                MeshletCount = sourceRange.MeshletCount,
                VertexIndexOffset = replacementVertexOffset,
                TriangleIndexOffset = replacementTriangleOffset,
            };
            return true;
        }

        private XRDataBuffer CreateMeshletRangeBufferSnapshot()
        {
            XRDataBuffer buffer = MakeMeshletRangeBuffer();
            uint requiredCount = MinMeshDataEntries;
            foreach (uint meshId in _meshletRangesByMeshId.Keys)
                requiredCount = Math.Max(requiredCount, meshId + 1u);
            EnsureSceneBufferCapacity(buffer, requiredCount, MinMeshDataEntries);
            for (uint index = 0; index < buffer.ElementCount; ++index)
                buffer.SetDataRawAtIndex(index, _meshletRangesByMeshId.GetValueOrDefault(index));
            buffer.PushSubData();
            return buffer;
        }

        private static XRDataBuffer CreateMeshletDescriptorBufferSnapshot(List<GpuMeshletDescriptor> descriptors)
        {
            XRDataBuffer buffer = MakeMeshletDescriptorBuffer();
            EnsureSceneBufferCapacity(buffer, ((uint)descriptors.Count).ClampMin(MinMeshletDescriptorEntries), MinMeshletDescriptorEntries);
            for (int index = 0; index < descriptors.Count; ++index)
                buffer.SetDataRawAtIndex((uint)index, descriptors[index]);
            buffer.PushSubData();
            return buffer;
        }

        private static XRDataBuffer CreateMeshletVertexIndexBufferSnapshot(List<uint> indices)
        {
            XRDataBuffer buffer = MakeMeshletVertexIndexBuffer();
            EnsureSceneBufferCapacity(buffer, ((uint)indices.Count).ClampMin(MinMeshletIndexEntries), MinMeshletIndexEntries);
            for (int index = 0; index < indices.Count; ++index)
                buffer.SetDataRawAtIndex((uint)index, indices[index]);
            buffer.PushSubData();
            return buffer;
        }

        private static XRDataBuffer CreateMeshletTriangleIndexBufferSnapshot(List<byte> indices)
        {
            XRDataBuffer buffer = MakeMeshletTriangleIndexBuffer();
            EnsureSceneBufferCapacity(buffer, ((uint)indices.Count).ClampMin(MinMeshletIndexEntries), MinMeshletIndexEntries);
            for (int index = 0; index < indices.Count; ++index)
                buffer.SetDataRawAtIndex((uint)index, indices[index]);
            buffer.PushSubData();
            return buffer;
        }

        private void DrainRetiredMeshletBufferGenerations(bool force)
        {
            while (_retiredMeshletBufferGenerations.Count > 0)
            {
                RetiredMeshletBufferGeneration retired = _retiredMeshletBufferGenerations.Peek();
                EGpuFenceStatus status = retired.Fence?.Poll() ?? EGpuFenceStatus.Signaled;
                if (!force && status == EGpuFenceStatus.Pending)
                    return;

                _retiredMeshletBufferGenerations.Dequeue();
                retired.Dispose();
                ++_meshletBufferGenerationRetireCount;
                RecordMeshletBufferGenerationTelemetry(rebuilds: 0UL, retires: 1UL);
            }
        }

        private void DestroyMeshletBufferGenerations()
        {
            // Initialize/Destroy are explicit renderer-quiescence boundaries. If a
            // scene is reinitialized while its renderer still exists, wait here
            // before releasing a generation whose retirement fence is pending.
            // When the renderer is already gone, backend teardown has necessarily
            // owned the corresponding device-idle contract.
            bool hasPendingRetirement = false;
            foreach (RetiredMeshletBufferGeneration retired in _retiredMeshletBufferGenerations)
            {
                if (retired.Fence?.Poll() != EGpuFenceStatus.Pending)
                    continue;

                hasPendingRetirement = true;
                break;
            }

            if (hasPendingRetirement)
                AbstractRenderer.Current?.WaitForGpu();

            DrainRetiredMeshletBufferGenerations(force: true);
            _meshletBufferGenerationDirty = true;
            _meshletBufferGeneration = 0UL;
            _meshletBufferGenerationRebuildCount = 0UL;
            _meshletBufferGenerationRetireCount = 0UL;
        }

        private ulong GetRetiredMeshletBufferBytes()
        {
            ulong bytes = 0UL;
            foreach (RetiredMeshletBufferGeneration generation in _retiredMeshletBufferGenerations)
                bytes += generation.ByteCount;
            return bytes;
        }

        private void RecordMeshletBufferGenerationTelemetry(ulong rebuilds, ulong retires)
        {
            RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletBufferGeneration(
                checked((long)MeshletBufferBytesResident),
                checked((long)GetRetiredMeshletBufferBytes()),
                checked((long)rebuilds),
                checked((long)retires));
        }

        private sealed class RetiredMeshletBufferGeneration(
            XRDataBuffer? rangeBuffer,
            XRDataBuffer? descriptorBuffer,
            XRDataBuffer? vertexIndexBuffer,
            XRDataBuffer? triangleIndexBuffer,
            XRGpuFence? fence) : IDisposable
        {
            public XRGpuFence? Fence { get; } = fence;
            public ulong ByteCount => GetBufferByteCount(rangeBuffer) + GetBufferByteCount(descriptorBuffer) + GetBufferByteCount(vertexIndexBuffer) + GetBufferByteCount(triangleIndexBuffer);

            public void Dispose()
            {
                rangeBuffer?.Destroy();
                descriptorBuffer?.Destroy();
                vertexIndexBuffer?.Destroy();
                triangleIndexBuffer?.Destroy();
                Fence?.Dispose();
            }
        }
    }
}
