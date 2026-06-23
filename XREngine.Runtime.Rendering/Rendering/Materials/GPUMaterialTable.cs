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
        private readonly Dictionary<ulong, uint> _handleIndicesByHandle = [];
        private readonly Dictionary<uint, ulong> _handlesByIndex = [];
        private readonly Dictionary<uint, uint> _handleRefCounts = [];
        private readonly Queue<uint> _freeHandleIndices = [];
        private readonly Queue<GPUMaterialRetiredHandle> _retiredHandles = [];
        private DirtyByteRange _materialDirtyBytes;
        private DirtyByteRange _textureHandleDirtyBytes;
        private uint _nextHandleIndex = InitialHandleIndex;

        public XRDataBuffer Buffer { get; }
        public XRDataBuffer TextureHandleBuffer { get; }
        public uint Capacity { get; private set; }
        public uint TextureHandleCapacity { get; private set; }
        public IReadOnlyCollection<uint> ActiveMaterialIds => _activeMaterialIds;
        public IReadOnlyCollection<ulong> ActiveTextureHandles => _handleIndicesByHandle.Keys;
        public GPUMaterialTableDirtyRange MaterialDirtyRange => _materialDirtyBytes.ToIndexRange(Buffer.ElementSize);
        public GPUMaterialTableDirtyRange TextureHandleDirtyRange => _textureHandleDirtyBytes.ToIndexRange(TextureHandleBuffer.ElementSize);

        public GPUMaterialTable(uint initialCapacity = 128, uint initialHandleCapacity = 256)
        {
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

            ReleaseMaterialHandleRefs(materialID);

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

            _activeMaterialIds.Add(materialID);
            return materialID;
        }

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

        public bool Remove(uint materialID)
        {
            if (materialID >= Capacity)
                return false;

            if (!_activeMaterialIds.Remove(materialID))
                return false;

            ReleaseMaterialHandleRefs(materialID);
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
            PushDirtyRange(Buffer, ref _materialDirtyBytes);
            PushDirtyRange(TextureHandleBuffer, ref _textureHandleDirtyBytes);
        }

        private void MarkMaterialRowDirty(uint rowIndex)
            => MarkRowDirty(ref _materialDirtyBytes, rowIndex, Buffer.ElementSize);

        private void MarkTextureHandleRowDirty(uint rowIndex)
            => MarkRowDirty(ref _textureHandleDirtyBytes, rowIndex, TextureHandleBuffer.ElementSize);

        private static void MarkRowDirty(ref DirtyByteRange range, uint rowIndex, uint rowSize)
        {
            ulong byteOffset64 = (ulong)rowIndex * rowSize;
            ulong byteEnd64 = byteOffset64 + rowSize;
            if (byteOffset64 > uint.MaxValue || byteEnd64 > uint.MaxValue)
                throw new InvalidOperationException("GPU material table dirty byte range exceeds supported buffer upload range.");

            range.Mark((uint)byteOffset64, (uint)rowSize);
        }

        private static void MarkFullDirty(ref DirtyByteRange range, XRDataBuffer buffer)
            => range.Mark(0u, buffer.Length);

        private static void PushDirtyRange(XRDataBuffer buffer, ref DirtyByteRange range)
        {
            if (!range.HasValue)
                return;

            uint offset = range.ByteOffset;
            uint length = range.ByteCount;
            range.Clear();

            if (length == 0u)
                return;

            if (offset == 0u && length >= buffer.Length)
            {
                buffer.PushSubData();
                return;
            }

            if (offset > (uint)int.MaxValue)
            {
                buffer.PushSubData();
                return;
            }

            buffer.PushSubData((int)offset, length);
        }

        private void Resize(uint newCapacity)
        {
            Buffer.Resize(newCapacity);
            Capacity = newCapacity;
            MarkFullDirty(ref _materialDirtyBytes, Buffer);
        }

        private void ResizeTextureHandleTable(uint newCapacity)
        {
            TextureHandleBuffer.Resize(newCapacity);
            TextureHandleCapacity = newCapacity;
            MarkFullDirty(ref _textureHandleDirtyBytes, TextureHandleBuffer);
        }

        public void Dispose()
        {
            Buffer?.Dispose();
            TextureHandleBuffer?.Dispose();
        }
    }
}
