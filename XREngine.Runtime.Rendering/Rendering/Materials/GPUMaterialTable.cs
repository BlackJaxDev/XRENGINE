using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Materials
{
    /// <summary>
    /// Manages the GPU material table and its second-level texture-handle table.
    /// </summary>
    public partial class GPUMaterialTable : XRBase, IDisposable
    {
        public const uint InvalidTextureHandleIndex = 0u;
        private const uint InitialHandleIndex = 1u;

        public static MaterialBindingLayout MaterialLayout => MaterialBindingLayouts.OpaqueDeferred;
        public static uint MaterialEntryUIntCount => MaterialLayout.RowWordCount;
        private readonly HashSet<uint> _activeMaterialIds = [];
        private readonly Dictionary<uint, GPUMaterialHandleIndices> _materialHandleIndices = [];
        private readonly Dictionary<uint, GPUMaterialEntry> _sourceMaterialEntries = [];
        private readonly Dictionary<uint, GPUMaterialTextureReferences> _sourceTextureReferences = [];
        private readonly Dictionary<ulong, uint> _handleIndicesByHandle = [];
        private readonly Dictionary<uint, ulong> _handlesByIndex = [];
        private readonly Dictionary<uint, uint> _handleRefCounts = [];
        private readonly Queue<uint> _freeHandleIndices = [];
        private readonly Queue<GPUMaterialRetiredHandle> _retiredHandles = [];
        private readonly object _publicationSync = new();
        private DirtyByteRange _materialDirtyBytes;
        private DirtyByteRange _textureHandleDirtyBytes;
        private SparseDirtyByteRanges _materialDirtyRanges;
        private SparseDirtyByteRanges _textureHandleDirtyRanges;
        private GPUMaterialTablePublication? _currentPublication;
        private static long s_nextPublicationOwnerId;
        private readonly ulong _publicationOwnerId = unchecked((ulong)Interlocked.Increment(ref s_nextPublicationOwnerId));
        private ulong _descriptorClosureGeneration;
        private GPUMaterialTablePublicationDelta _lastPublicationDelta;
        private bool _hasPublicationDelta;
        private uint _nextHandleIndex = InitialHandleIndex;
        private ulong _publicationGeneration;

        public XRDataBuffer Buffer { get; }
        public XRDataBuffer TextureHandleBuffer { get; }
        public uint Capacity { get; private set; }
        public uint TextureHandleCapacity { get; private set; }
        public IReadOnlyCollection<uint> ActiveMaterialIds => _activeMaterialIds;
        public IReadOnlyCollection<ulong> ActiveTextureHandles => _handleIndicesByHandle.Keys;
        public ulong PublicationGeneration => _publicationGeneration;
        public GPUMaterialTableDirtyRange MaterialDirtyRange => _materialDirtyBytes.ToIndexRange(Buffer.ElementSize);
        public GPUMaterialTableDirtyRange TextureHandleDirtyRange => _textureHandleDirtyBytes.ToIndexRange(TextureHandleBuffer.ElementSize);

        /// <summary>
        /// Retains the immutable CPU publication matching the last successful table upload.
        /// The caller must dispose the returned token when its sealed work is released.
        /// </summary>
        internal bool TryRetainCurrentPublication(out GPUMaterialTablePublication publication)
        {
            lock (_publicationSync)
            {
                if (_currentPublication is not { } current)
                {
                    publication = null!;
                    return false;
                }

                publication = current.Retain();
                return true;
            }
        }
        /// <summary>
        /// Gets the last successful CPU-to-GPU table publication summary. This is a cold diagnostic surface;
        /// range detail is available through the copy methods below.
        /// </summary>
        public bool TryGetLastPublicationDelta(out GPUMaterialTablePublicationDelta delta)
        {
            delta = _lastPublicationDelta;
            return _hasPublicationDelta;
        }

        /// <summary>
        /// Copies the exact sparse material-row ranges published by the last table update.
        /// </summary>
        public int CopyLastPublicationMaterialRanges(Span<GPUMaterialTableDirtyRange> destination)
            => _materialDirtyRanges.CopyLastPublishedRanges(destination, Buffer.ElementSize);

        /// <summary>
        /// Copies the exact sparse texture-handle ranges published by the last table update.
        /// </summary>
        public int CopyLastPublicationTextureHandleRanges(Span<GPUMaterialTableDirtyRange> destination)
            => _textureHandleDirtyRanges.CopyLastPublishedRanges(destination, TextureHandleBuffer.ElementSize);

        public GPUMaterialTable(uint initialCapacity = 128, uint initialHandleCapacity = 256)
        {
            if (MaterialEntryUIntCount != GPUMaterialEntryWords.WordCount ||
                Marshal.SizeOf<GPUMaterialEntryWords>() != GPUMaterialEntryWords.WordCount * sizeof(uint))
            {
                throw new InvalidOperationException(
                    $"GPU material row layout mismatch: layout={MaterialEntryUIntCount} words, " +
                    $"upload={GPUMaterialEntryWords.WordCount} words/{Marshal.SizeOf<GPUMaterialEntryWords>()} bytes.");
            }

            Capacity = initialCapacity;
            Buffer = new XRDataBuffer(
                "MaterialTable",
                EBufferTarget.ShaderStorageBuffer,
                Capacity,
                EComponentType.UInt,
                MaterialEntryUIntCount,
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false
            };
            Buffer.Generate();

            TextureHandleCapacity = Math.Max(initialHandleCapacity, InitialHandleIndex);
            TextureHandleBuffer = new XRDataBuffer(
                "MaterialTextureHandleTable",
                EBufferTarget.ShaderStorageBuffer,
                TextureHandleCapacity,
                EComponentType.UInt,
                4,
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false
            };
            TextureHandleBuffer.Generate();
        }

        public uint AddOrUpdate(uint materialID, GPUMaterialEntry entry)
            => AddOrUpdate(materialID, entry, GPUMaterialTextureReferences.Empty);

        public uint AddOrUpdate(uint materialID, GPUMaterialEntry entry, GPUMaterialTextureHandles textureHandles)
            => AddOrUpdate(materialID, entry, GPUMaterialTextureReferences.FromOpenGLHandles(textureHandles));

        public uint AddOrUpdate(uint materialID, GPUMaterialEntry entry, GPUMaterialTextureReferences textureReferences)
        {
            if (materialID >= Capacity)
                Resize(Math.Max(Capacity * 2, materialID + 1));
            if (MaterialStateMatches(materialID, entry, textureReferences))
                return materialID;

            ReleaseMaterialHandleRefs(materialID);

            GPUMaterialEntry sourceEntry = entry;

            GPUMaterialHandleIndices indices = new(
                ResolveTextureReference(textureReferences.Albedo, out uint albedoHandleIndex),
                ResolveTextureReference(textureReferences.Normal, out uint normalHandleIndex),
                ResolveTextureReference(textureReferences.RM, out uint rmHandleIndex));

            entry.AlbedoHandleIndex = ResolveShaderTextureIndex(textureReferences.Albedo, albedoHandleIndex);
            entry.NormalHandleIndex = ResolveShaderTextureIndex(textureReferences.Normal, normalHandleIndex);
            entry.RMHandleIndex = ResolveShaderTextureIndex(textureReferences.RM, rmHandleIndex);

            Buffer.SetDataRawAtIndex(materialID, PackMaterialEntry(entry));
            MarkMaterialRowDirty(materialID);

            if (!indices.Equals(GPUMaterialHandleIndices.Empty))
                _materialHandleIndices[materialID] = indices;

            _sourceMaterialEntries[materialID] = sourceEntry;
            _sourceTextureReferences[materialID] = textureReferences;
            _activeMaterialIds.Add(materialID);
            return materialID;
        }

        private bool MaterialStateMatches(
            uint materialID,
            in GPUMaterialEntry entry,
            in GPUMaterialTextureReferences textureReferences)
            => _activeMaterialIds.Contains(materialID) &&
               _sourceMaterialEntries.TryGetValue(materialID, out GPUMaterialEntry existingEntry) &&
               _sourceTextureReferences.TryGetValue(materialID, out GPUMaterialTextureReferences existingReferences) &&
               MaterialEntriesEqual(existingEntry, entry) &&
               existingReferences.Equals(textureReferences);

        private static bool MaterialEntriesEqual(in GPUMaterialEntry left, in GPUMaterialEntry right)
            => left.AlbedoHandleIndex == right.AlbedoHandleIndex &&
               left.NormalHandleIndex == right.NormalHandleIndex &&
               left.RMHandleIndex == right.RMHandleIndex &&
               left.Flags == right.Flags &&
               left.BaseColorOpacity == right.BaseColorOpacity &&
               left.RMSE == right.RMSE &&
               left.AlphaCutoff == right.AlphaCutoff;

        private uint ResolveTextureReference(GPUMaterialTextureReference reference, out uint openGlHandleIndex)
        {
            openGlHandleIndex = InvalidTextureHandleIndex;
            if (reference.Kind == EGPUMaterialTextureReferenceKind.OpenGLBindlessHandle)
                openGlHandleIndex = AddHandleReference(reference.Payload);

            return openGlHandleIndex;
        }

        private static uint ResolveShaderTextureIndex(GPUMaterialTextureReference reference, uint openGlHandleIndex)
            => reference.Kind switch
            {
                EGPUMaterialTextureReferenceKind.OpenGLBindlessHandle => openGlHandleIndex,
                EGPUMaterialTextureReferenceKind.VulkanDescriptorIndex => reference.VulkanDescriptorIndex,
                _ => InvalidTextureHandleIndex,
            };

        public bool IsActive(uint materialID)
            => _activeMaterialIds.Contains(materialID);

        public bool Remove(uint materialID)
        {
            if (materialID >= Capacity)
                return false;

            if (!_activeMaterialIds.Remove(materialID))
                return false;

            ReleaseMaterialHandleRefs(materialID);
            _sourceMaterialEntries.Remove(materialID);
            _sourceTextureReferences.Remove(materialID);
            Buffer.SetDataRawAtIndex(materialID, default(GPUMaterialEntryWords));
            MarkMaterialRowDirty(materialID);
            return true;
        }

        public bool TryConsumeRetiredHandle(out GPUMaterialRetiredHandle retiredHandle)
            => _retiredHandles.TryDequeue(out retiredHandle);

        public uint TrimTrailingUnused(uint minimumCapacity = 128u)
        {
            uint safeMinimum = Math.Max(1u, minimumCapacity);
            uint maxActive = 0u;
            foreach (uint materialID in _activeMaterialIds)
            {
                if (materialID > maxActive)
                    maxActive = materialID;
            }

            uint targetCapacity = Math.Max(safeMinimum, maxActive + 1u);
            if (targetCapacity >= Capacity)
                return Capacity;

            Resize(targetCapacity);
            return Capacity;
        }

        private uint AddHandleReference(ulong handle)
        {
            if (handle == 0ul)
                return InvalidTextureHandleIndex;

            if (!_handleIndicesByHandle.TryGetValue(handle, out uint index))
            {
                index = AllocateHandleIndex();
                _handleIndicesByHandle.Add(handle, index);
                _handlesByIndex.Add(index, handle);

                TextureHandleBuffer.SetDataRawAtIndex(index, PackHandleEntry(new GPUTextureHandleEntry
                {
                    Handle = handle,
                    Flags = 1u,
                    Padding0 = 0u
                }));
                MarkTextureHandleRowDirty(index);
            }

            _handleRefCounts.TryGetValue(index, out uint refCount);
            _handleRefCounts[index] = refCount + 1u;
            return index;
        }

        private uint AllocateHandleIndex()
        {
            uint index = _freeHandleIndices.Count > 0
                ? _freeHandleIndices.Dequeue()
                : _nextHandleIndex++;

            if (index >= TextureHandleCapacity)
                ResizeTextureHandleTable(Math.Max(TextureHandleCapacity * 2, index + 1u));

            return index;
        }

        private void ReleaseMaterialHandleRefs(uint materialID)
        {
            if (!_materialHandleIndices.Remove(materialID, out GPUMaterialHandleIndices indices))
                return;

            ReleaseHandleReference(indices.Albedo);
            ReleaseHandleReference(indices.Normal);
            ReleaseHandleReference(indices.RM);
        }

        private void ReleaseHandleReference(uint index)
        {
            if (index == InvalidTextureHandleIndex)
                return;

            if (!_handleRefCounts.TryGetValue(index, out uint refCount))
                return;

            if (refCount > 1u)
            {
                _handleRefCounts[index] = refCount - 1u;
                return;
            }

            _handleRefCounts.Remove(index);
            if (_handlesByIndex.Remove(index, out ulong handle))
            {
                _handleIndicesByHandle.Remove(handle);
                _retiredHandles.Enqueue(new GPUMaterialRetiredHandle(handle));
            }

            TextureHandleBuffer.SetDataRawAtIndex(index, default(GPUTextureHandleEntryWords));
            MarkTextureHandleRowDirty(index);
            _freeHandleIndices.Enqueue(index);
        }

        private static GPUTextureHandleEntryWords PackHandleEntry(GPUTextureHandleEntry entry)
            => new()
            {
                HandleLo = (uint)(entry.Handle & 0xFFFFFFFFul),
                HandleHi = (uint)(entry.Handle >> 32),
                Flags = entry.Flags,
                Padding0 = entry.Padding0,
            };

        private static GPUMaterialEntryWords PackMaterialEntry(GPUMaterialEntry entry)
        {
            GPUMaterialEntryWords words = new();
            Span<uint> row = MemoryMarshal.CreateSpan(ref words.AlbedoHandleIndex, GPUMaterialEntryWords.WordCount);
            if (!MaterialBindingRowPacker.TryWriteOpaqueDeferred(MaterialLayout, entry, row, out string error))
                throw new InvalidOperationException(error);

            return words;
        }

        public void PushDirtyRanges()
        {
            bool publishesChanges = _materialDirtyBytes.HasValue || _textureHandleDirtyBytes.HasValue;
            if (!publishesChanges)
                return;

            GPUMaterialTablePublication? previousPublication;
            lock (_publicationSync)
                previousPublication = _currentPublication?.Retain();

            GPUMaterialTablePublication? publication = null;

            try
            {
                ulong nextPublicationGeneration = checked(_publicationGeneration + 1u);
                GPUMaterialTextureReference[] closureReferences =
                    GPUMaterialTablePublication.CaptureVulkanTextureReferences(_sourceTextureReferences);
                ulong nextDescriptorClosureGeneration =
                    previousPublication is not null &&
                    previousPublication.HasSameDescriptorClosure(closureReferences)
                        ? previousPublication.DescriptorClosureGeneration
                        : checked(_descriptorClosureGeneration + 1u);
                publication = GPUMaterialTablePublication.Capture(
                    Buffer,
                    previousPublication,
                    _materialDirtyRanges,
                    closureReferences,
                    _publicationOwnerId,
                    nextPublicationGeneration,
                    nextDescriptorClosureGeneration,
                    MaterialEntryUIntCount);
                PublicationRangeCounts material = PushDirtyRanges(
                    Buffer,
                    ref _materialDirtyBytes,
                    ref _materialDirtyRanges);
                PublicationRangeCounts textureHandles = PushDirtyRanges(
                    TextureHandleBuffer,
                    ref _textureHandleDirtyBytes,
                    ref _textureHandleDirtyRanges);
                _publicationGeneration = nextPublicationGeneration;
                GPUMaterialTablePublication? replacedPublication;
                lock (_publicationSync)
                {
                    replacedPublication = _currentPublication;
                    _currentPublication = publication;
                }
                publication = null; // Ownership moved to the table, even if releasing its predecessor fails.
                replacedPublication?.Dispose();
                _descriptorClosureGeneration = nextDescriptorClosureGeneration;
                _lastPublicationDelta = new GPUMaterialTablePublicationDelta(
                    _publicationGeneration,
                    Capacity,
                    TextureHandleCapacity,
                    material.RangeCount,
                    material.RowCount,
                    material.ByteCount,
                    textureHandles.RangeCount,
                    textureHandles.RowCount,
                    textureHandles.ByteCount);
                _hasPublicationDelta = true;
            }
            catch
            {
                publication?.Dispose();
                throw;
            }
            finally
            {
                previousPublication?.Dispose();
            }
        }

        private void MarkMaterialRowDirty(uint rowIndex)
            => MarkRowDirty(ref _materialDirtyBytes, ref _materialDirtyRanges, rowIndex, Buffer.ElementSize);

        private void MarkTextureHandleRowDirty(uint rowIndex)
            => MarkRowDirty(ref _textureHandleDirtyBytes, ref _textureHandleDirtyRanges, rowIndex, TextureHandleBuffer.ElementSize);

        private static void MarkRowDirty(
            ref DirtyByteRange range,
            ref SparseDirtyByteRanges sparseRanges,
            uint rowIndex,
            uint rowSize)
        {
            ulong byteOffset64 = (ulong)rowIndex * rowSize;
            ulong byteEnd64 = byteOffset64 + rowSize;
            if (byteOffset64 > uint.MaxValue || byteEnd64 > uint.MaxValue)
                throw new InvalidOperationException("GPU material table dirty byte range exceeds supported buffer upload range.");

            range.Mark((uint)byteOffset64, (uint)rowSize);
            sparseRanges.Mark((uint)byteOffset64, rowSize);
        }

        private static void MarkFullDirty(
            ref DirtyByteRange range,
            ref SparseDirtyByteRanges sparseRanges,
            XRDataBuffer buffer)
        {
            range.Mark(0u, buffer.Length);
            sparseRanges.MarkFull(buffer.Length);
        }

        private static PublicationRangeCounts PushDirtyRanges(
            XRDataBuffer buffer,
            ref DirtyByteRange aggregateRange,
            ref SparseDirtyByteRanges sparseRanges)
        {
            if (!aggregateRange.HasValue)
                return default;

            PublicationRangeCounts result = sparseRanges.GetPublicationRangeCounts(buffer.ElementSize);
            if (sparseRanges.IsFullDirty)
            {
                buffer.PushSubData();
            }
            else
            {
                ReadOnlySpan<DirtyByteRange> ranges = sparseRanges.Ranges;
                for (int index = 0; index < ranges.Length; ++index)
                {
                    DirtyByteRange range = ranges[index];
                    if (range.ByteOffset > (uint)int.MaxValue)
                    {
                        buffer.PushSubData();
                        result = PublicationRangeCounts.Full(buffer.Length, buffer.ElementSize);
                        break;
                    }

                    buffer.PushSubData((int)range.ByteOffset, range.ByteCount);
                }
            }

            sparseRanges.CapturePublishedRanges();
            sparseRanges.Clear();
            aggregateRange.Clear();
            return result;
        }

        private void Resize(uint newCapacity)
        {
            Buffer.Resize(newCapacity);
            Capacity = newCapacity;
            MarkFullDirty(ref _materialDirtyBytes, ref _materialDirtyRanges, Buffer);
        }

        private void ResizeTextureHandleTable(uint newCapacity)
        {
            TextureHandleBuffer.Resize(newCapacity);
            TextureHandleCapacity = newCapacity;
            MarkFullDirty(ref _textureHandleDirtyBytes, ref _textureHandleDirtyRanges, TextureHandleBuffer);
        }

        public void Dispose()
        {
            GPUMaterialTablePublication? publication;
            lock (_publicationSync)
            {
                publication = _currentPublication;
                _currentPublication = null;
            }
            publication?.Dispose();
            Buffer?.Dispose();
            TextureHandleBuffer?.Dispose();
        }
    }
}
