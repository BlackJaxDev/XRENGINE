using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Materials
{
    /// <summary>
    /// GPU material table entry (bindless handles split into two uints each for std430 alignment).
    /// Extend as needed (PBR params, etc.). Keep size a multiple of 16 bytes.
    /// </summary>
    public struct GPUMaterialEntry
    {
        public ulong AlbedoHandle;      // 8 bytes
        public ulong NormalHandle;      // 8 bytes
        public ulong RMHandle;          // 8 bytes (Roughness/Metal/AO)
        public uint Flags;              // 4 bytes
        public uint Padding0;           // 4
        public uint Padding1;           // 4
        public uint Padding2;           // 4 (total 40 -> align to 48 if needed)
    }

    /// <summary>
    /// Manages a GPU material table SSBO for bindless sampling.
    /// </summary>
    public class GPUMaterialTable : XRBase, IDisposable
    {
        private static readonly uint[] EmptyEntryWords = new uint[12];
        private readonly HashSet<uint> _activeMaterialIds = [];

        public XRDataBuffer Buffer { get; }
        public uint Capacity { get; private set; }
        public IReadOnlyCollection<uint> ActiveMaterialIds => _activeMaterialIds;

        public GPUMaterialTable(uint initialCapacity = 128)
        {
            Capacity = initialCapacity;
            Buffer = new XRDataBuffer(
                "MaterialTable",
                EBufferTarget.ShaderStorageBuffer,
                Capacity,
                EComponentType.UInt,
                12, // 12 uints (48 bytes) per entry (to hold the 3x64-bit + 4x uint fields when reinterpreted)
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false
            };
            Buffer.Generate();
        }

        public uint AddOrUpdate(uint materialID, GPUMaterialEntry entry)
        {
            if (materialID >= Capacity)
                Resize(Math.Max(Capacity * 2, materialID + 1));

            // Convert entry to 12 uints
            Span<uint> scratch = stackalloc uint[12];
            PackULong(entry.AlbedoHandle, scratch, 0);
            PackULong(entry.NormalHandle, scratch, 2);
            PackULong(entry.RMHandle, scratch, 4);
            scratch[6] = entry.Flags;
            scratch[7] = entry.Padding0;
            scratch[8] = entry.Padding1;
            scratch[9] = entry.Padding2;
            scratch[10] = 0u;
            scratch[11] = 0u;
            Buffer.SetDataArrayRawAtIndex(materialID, scratch.ToArray());
            _activeMaterialIds.Add(materialID);
            return materialID;
        }

        public bool Remove(uint materialID)
        {
            if (materialID >= Capacity)
                return false;

            if (!_activeMaterialIds.Remove(materialID))
                return false;

            Buffer.SetDataArrayRawAtIndex(materialID, EmptyEntryWords);
            return true;
        }

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

        private void PackULong(ulong value, Span<uint> dst, int offset)
        {
            dst[offset] = (uint)(value & 0xFFFFFFFFul);
            dst[offset + 1] = (uint)(value >> 32);
        }

        private void Resize(uint newCapacity)
        {
            Buffer.Resize(newCapacity);
            Capacity = newCapacity;
        }

        public void Dispose()
        {
            Buffer?.Dispose();
        }
    }
}
