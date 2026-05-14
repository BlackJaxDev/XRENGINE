using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Materials
{
    /// <summary>
    /// GPU material table entry. Texture fields are indices into <see cref="GPUMaterialTable.TextureHandleBuffer"/>,
    /// not API handles. This keeps the per-material row small and lets GL bindless handles or Vulkan descriptor
    /// indices share the same shader-facing indirection contract.
    /// </summary>
    public struct GPUMaterialEntry
    {
        public uint AlbedoHandleIndex;
        public uint NormalHandleIndex;
        public uint RMHandleIndex;
        public uint Flags;
        public Vector4 BaseColorOpacity;
        public Vector4 RMSE;
    }

    /// <summary>
    /// Backend texture handles referenced by <see cref="GPUMaterialEntry"/>.
    /// OpenGL stores ARB_bindless_texture handles split into low/high uints. Vulkan uses the same
    /// index as the descriptor-array slot and leaves the 64-bit handle zeroed.
    /// </summary>
    public struct GPUTextureHandleEntry
    {
        public ulong Handle;
        public uint Flags;
        public uint Padding0;
    }

    public readonly record struct GPUMaterialTextureHandles(ulong Albedo, ulong Normal, ulong RM);

    public readonly record struct GPUMaterialRetiredHandle(ulong Handle);

    public readonly record struct GPUMaterialTableUpdate(uint MaterialID, GPUMaterialEntry Entry);

    public readonly record struct GPUMaterialHandleTableUpdate(uint HandleIndex, GPUTextureHandleEntry Entry);

    public readonly record struct GPUMaterialHandleIndices(uint Albedo, uint Normal, uint RM)
    {
        public static readonly GPUMaterialHandleIndices Empty = new(0u, 0u, 0u);
    }

    /// <summary>
    /// Manages the GPU material table and its second-level texture-handle table.
    /// </summary>
    public class GPUMaterialTable : XRBase, IDisposable
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
        private uint _nextHandleIndex = InitialHandleIndex;

        public XRDataBuffer Buffer { get; }
        public XRDataBuffer TextureHandleBuffer { get; }
        public uint Capacity { get; private set; }
        public uint TextureHandleCapacity { get; private set; }
        public IReadOnlyCollection<uint> ActiveMaterialIds => _activeMaterialIds;
        public IReadOnlyCollection<ulong> ActiveTextureHandles => _handleIndicesByHandle.Keys;

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
            => AddOrUpdate(materialID, entry, new GPUMaterialTextureHandles());

        public uint AddOrUpdate(uint materialID, GPUMaterialEntry entry, GPUMaterialTextureHandles textureHandles)
        {
            if (materialID >= Capacity)
                Resize(Math.Max(Capacity * 2, materialID + 1));

            ReleaseMaterialHandleRefs(materialID);

            GPUMaterialHandleIndices indices = new(
                AddHandleReference(textureHandles.Albedo),
                AddHandleReference(textureHandles.Normal),
                AddHandleReference(textureHandles.RM));

            entry.AlbedoHandleIndex = indices.Albedo;
            entry.NormalHandleIndex = indices.Normal;
            entry.RMHandleIndex = indices.RM;

            Buffer.SetDataRawAtIndex(materialID, PackMaterialEntry(entry));

            if (!indices.Equals(GPUMaterialHandleIndices.Empty))
                _materialHandleIndices[materialID] = indices;

            _activeMaterialIds.Add(materialID);
            return materialID;
        }

        public bool Remove(uint materialID)
        {
            if (materialID >= Capacity)
                return false;

            if (!_activeMaterialIds.Remove(materialID))
                return false;

            ReleaseMaterialHandleRefs(materialID);
            Buffer.SetDataRawAtIndex(materialID, default(GPUMaterialEntryWords));
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

        [StructLayout(LayoutKind.Sequential)]
        private struct GPUMaterialEntryWords
        {
            public const int WordCount = 12;

            public uint AlbedoHandleIndex;
            public uint NormalHandleIndex;
            public uint RMHandleIndex;
            public uint Flags;
            public uint BaseColorX;
            public uint BaseColorY;
            public uint BaseColorZ;
            public uint Opacity;
            public uint Roughness;
            public uint Metallic;
            public uint Specular;
            public uint Emission;
        }

        private struct GPUTextureHandleEntryWords
        {
            public uint HandleLo;
            public uint HandleHi;
            public uint Flags;
            public uint Padding0;
        }

        private void Resize(uint newCapacity)
        {
            Buffer.Resize(newCapacity);
            Capacity = newCapacity;
        }

        private void ResizeTextureHandleTable(uint newCapacity)
        {
            TextureHandleBuffer.Resize(newCapacity);
            TextureHandleCapacity = newCapacity;
        }

        public void Dispose()
        {
            Buffer?.Dispose();
            TextureHandleBuffer?.Dispose();
        }
    }
}
