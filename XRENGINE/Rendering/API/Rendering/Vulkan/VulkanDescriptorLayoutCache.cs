using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal const uint DescriptorSetGlobals = 0;
    internal const uint DescriptorSetCompute = 1;
    internal const uint DescriptorSetMaterial = 2;
    internal const uint DescriptorSetPerPass = 3;
    internal const uint DescriptorSetTierCount = 4;

    private readonly object _descriptorSetLayoutCacheLock = new();
    private readonly Dictionary<ulong, List<CachedDescriptorSetLayout>> _descriptorSetLayoutsByHash = new();
    private readonly Dictionary<ulong, CachedDescriptorSetLayout> _descriptorSetLayoutsByHandle = new();

    private sealed class CachedDescriptorSetLayout
    {
        public required DescriptorSetLayout Layout;
        public required DescriptorLayoutBindingSignature[] Signature;
        public required ulong SchemaHash;
        public required bool UsesUpdateAfterBind;
        public int RefCount;
    }

    private readonly record struct DescriptorLayoutBindingSignature(
        uint Binding,
        DescriptorType DescriptorType,
        uint DescriptorCount,
        ShaderStageFlags StageFlags);

    internal bool SupportsDescriptorIndexing => _supportsDescriptorIndexing;

    internal bool TryAcquireCachedDescriptorSetLayout(
        DescriptorSetLayoutBinding[] bindings,
        out DescriptorSetLayout layout,
        out bool usesUpdateAfterBind)
    {
        layout = default;
        usesUpdateAfterBind = false;

        DescriptorLayoutBindingSignature[] signature = BuildLayoutSignature(bindings);
        ulong schemaHash = ComputeLayoutSchemaHash(signature);

        lock (_descriptorSetLayoutCacheLock)
        {
            if (_descriptorSetLayoutsByHash.TryGetValue(schemaHash, out List<CachedDescriptorSetLayout>? bucket))
            {
                foreach (CachedDescriptorSetLayout cached in bucket)
                {
                    if (!LayoutSignaturesEqual(cached.Signature, signature))
                        continue;

                    cached.RefCount++;
                    layout = cached.Layout;
                    usesUpdateAfterBind = cached.UsesUpdateAfterBind;
                    return true;
                }
            }

            if (!TryCreateDescriptorSetLayout(bindings, out layout, out usesUpdateAfterBind))
                return false;

            CachedDescriptorSetLayout created = new()
            {
                Layout = layout,
                Signature = signature,
                SchemaHash = schemaHash,
                UsesUpdateAfterBind = usesUpdateAfterBind,
                RefCount = 1
            };

            bucket ??= [];
            bucket.Add(created);
            _descriptorSetLayoutsByHash[schemaHash] = bucket;
            _descriptorSetLayoutsByHandle[layout.Handle] = created;
            return true;
        }
    }

    internal void ReleaseCachedDescriptorSetLayout(DescriptorSetLayout layout)
    {
        if (layout.Handle == 0)
            return;

        lock (_descriptorSetLayoutCacheLock)
        {
            if (!_descriptorSetLayoutsByHandle.TryGetValue(layout.Handle, out CachedDescriptorSetLayout? cached))
            {
                Api!.DestroyDescriptorSetLayout(device, layout, null);
                return;
            }

            cached.RefCount--;
            if (cached.RefCount > 0)
                return;

            _descriptorSetLayoutsByHandle.Remove(layout.Handle);
            if (_descriptorSetLayoutsByHash.TryGetValue(cached.SchemaHash, out List<CachedDescriptorSetLayout>? bucket))
            {
                bucket.Remove(cached);
                if (bucket.Count == 0)
                    _descriptorSetLayoutsByHash.Remove(cached.SchemaHash);
            }

            Api!.DestroyDescriptorSetLayout(device, cached.Layout, null);
        }
    }

    private void DestroyCachedDescriptorSetLayouts()
    {
        lock (_descriptorSetLayoutCacheLock)
        {
            foreach (CachedDescriptorSetLayout cached in _descriptorSetLayoutsByHandle.Values)
            {
                if (cached.Layout.Handle != 0)
                    Api!.DestroyDescriptorSetLayout(device, cached.Layout, null);
            }

            _descriptorSetLayoutsByHash.Clear();
            _descriptorSetLayoutsByHandle.Clear();
        }
    }

    private static DescriptorLayoutBindingSignature[] BuildLayoutSignature(DescriptorSetLayoutBinding[] bindings)
    {
        DescriptorLayoutBindingSignature[] signature = new DescriptorLayoutBindingSignature[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            DescriptorSetLayoutBinding binding = bindings[i];
            signature[i] = new DescriptorLayoutBindingSignature(
                binding.Binding,
                binding.DescriptorType,
                binding.DescriptorCount,
                binding.StageFlags);
        }

        return signature;
    }

    private static ulong ComputeLayoutSchemaHash(DescriptorLayoutBindingSignature[] signature)
    {
        ulong hash = 1469598103934665603UL;

        static void Mix(ref ulong value, ulong part)
        {
            value ^= part;
            value *= 1099511628211UL;
        }

        foreach (DescriptorLayoutBindingSignature part in signature)
        {
            Mix(ref hash, part.Binding);
            Mix(ref hash, (ulong)part.DescriptorType);
            Mix(ref hash, part.DescriptorCount);
            Mix(ref hash, (ulong)part.StageFlags);
        }

        return hash;
    }

    private static bool LayoutSignaturesEqual(DescriptorLayoutBindingSignature[] left, DescriptorLayoutBindingSignature[] right)
    {
        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }

    private bool TryCreateDescriptorSetLayout(
        DescriptorSetLayoutBinding[] bindings,
        out DescriptorSetLayout layout,
        out bool usesUpdateAfterBind)
    {
        layout = default;
        usesUpdateAfterBind = false;

        DescriptorSetLayoutCreateFlags flags = 0;
        DescriptorSetLayoutBindingFlagsCreateInfo bindingFlagsInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo
        };

        DescriptorBindingFlags[]? bindingFlags = null;
        if (_supportsDescriptorIndexing && bindings.Length > 0)
        {
            bindingFlags = new DescriptorBindingFlags[bindings.Length];
            for (int i = 0; i < bindings.Length; i++)
            {
                bindingFlags[i] = DescriptorBindingFlags.UpdateAfterBindBit | DescriptorBindingFlags.PartiallyBoundBit;
            }

            fixed (DescriptorBindingFlags* bindingFlagsPtr = bindingFlags)
            {
                bindingFlagsInfo.BindingCount = (uint)bindingFlags.Length;
                bindingFlagsInfo.PBindingFlags = bindingFlagsPtr;
                usesUpdateAfterBind = true;
                flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit;

                fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
                {
                    DescriptorSetLayoutCreateInfo layoutInfo = new()
                    {
                        SType = StructureType.DescriptorSetLayoutCreateInfo,
                        Flags = flags,
                        BindingCount = (uint)bindings.Length,
                        PBindings = bindingsPtr,
                        PNext = &bindingFlagsInfo
                    };

                    if (Api!.CreateDescriptorSetLayout(device, ref layoutInfo, null, out layout) != Result.Success)
                        return false;
                }
            }

            return true;
        }

        fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
        {
            DescriptorSetLayoutCreateInfo layoutInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingsPtr,
            };

            if (Api!.CreateDescriptorSetLayout(device, ref layoutInfo, null, out layout) != Result.Success)
                return false;
        }

        return true;
    }
}
