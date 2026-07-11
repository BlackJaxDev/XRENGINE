using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// The index of the global descriptor set.
    /// </summary>
    internal const uint DescriptorSetGlobals = 0;
    /// <summary>
    /// The index of the compute descriptor set.
    /// </summary>
    internal const uint DescriptorSetCompute = 1;
    /// <summary>
    /// The index of the material descriptor set.
    /// </summary>
    internal const uint DescriptorSetMaterial = 2;
    /// <summary>
    /// The index of the per-pass descriptor set.
    /// </summary>
    internal const uint DescriptorSetPerPass = 3;
    /// <summary>
    /// The total number of descriptor set tiers.
    /// </summary>
    internal const uint DescriptorSetTierCount = 4;

    private readonly object _descriptorSetLayoutCacheLock = new();
    private readonly Dictionary<ulong, List<CachedDescriptorSetLayout>> _descriptorSetLayoutsByHash = new();
    private readonly Dictionary<ulong, CachedDescriptorSetLayout> _descriptorSetLayoutsByHandle = new();

    /// <summary>
    /// Indicates whether the Vulkan renderer supports descriptor indexing.
    /// </summary>
    internal bool SupportsDescriptorIndexing => _supportsDescriptorIndexing;

    /// <summary>
    /// Tries to acquire a cached descriptor set layout for the specified set index and bindings.
    /// </summary>
    /// <param name="setIndex">The index of the descriptor set.</param>
    /// <param name="bindings">The array of descriptor set layout bindings.</param>
    /// <param name="layout">The acquired descriptor set layout if successful.</param>
    /// <param name="usesUpdateAfterBind">Indicates whether the layout uses update-after-bind.</param>
    /// <param name="usesVariableDescriptorCount">Indicates whether the layout uses variable descriptor count.</param>
    /// <returns>True if a cached descriptor set layout was successfully acquired; otherwise, false.</returns>
    internal bool TryAcquireCachedDescriptorSetLayout(
        uint setIndex,
        DescriptorSetLayoutBinding[] bindings,
        out DescriptorSetLayout layout,
        out bool usesUpdateAfterBind,
        out bool usesVariableDescriptorCount)
    {
        layout = default;
        usesUpdateAfterBind = false;
        usesVariableDescriptorCount = false;

        DescriptorLayoutBindingSignature[] signature = BuildLayoutSignature(setIndex, bindings);
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
                    usesVariableDescriptorCount = cached.UsesVariableDescriptorCount;
                    return true;
                }
            }

            if (!TryCreateDescriptorSetLayout(setIndex, bindings, out layout, out usesUpdateAfterBind, out usesVariableDescriptorCount))
                return false;

            TrackLiveDescriptorSetLayout(layout, $"DescriptorLayoutCache.Set{setIndex}");

            CachedDescriptorSetLayout created = new()
            {
                Layout = layout,
                Signature = signature,
                SchemaHash = schemaHash,
                UsesUpdateAfterBind = usesUpdateAfterBind,
                UsesVariableDescriptorCount = usesVariableDescriptorCount,
                RefCount = 1
            };

            bucket ??= [];
            bucket.Add(created);
            _descriptorSetLayoutsByHash[schemaHash] = bucket;
            _descriptorSetLayoutsByHandle[layout.Handle] = created;
            return true;
        }
    }

    /// <summary>
    /// Releases a cached descriptor set layout, decrementing its reference count and destroying it if no longer in use.
    /// </summary>
    /// <param name="layout">The descriptor set layout to release.</param>
    internal void ReleaseCachedDescriptorSetLayout(DescriptorSetLayout layout)
    {
        if (layout.Handle == 0)
            return;

        lock (_descriptorSetLayoutCacheLock)
        {
            if (!_descriptorSetLayoutsByHandle.TryGetValue(layout.Handle, out CachedDescriptorSetLayout? cached))
            {
                if (TryBeginDestroyDescriptorSetLayout(layout, "DescriptorLayoutCache.UncachedRelease"))
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

            if (TryBeginDestroyDescriptorSetLayout(cached.Layout, "DescriptorLayoutCache.Release"))
                Api!.DestroyDescriptorSetLayout(device, cached.Layout, null);
        }
    }

    /// <summary>
    /// Destroys all cached descriptor set layouts, releasing their resources.
    /// </summary>
    private void DestroyCachedDescriptorSetLayouts()
    {
        lock (_descriptorSetLayoutCacheLock)
        {
            foreach (CachedDescriptorSetLayout cached in _descriptorSetLayoutsByHandle.Values)
            {
                if (cached.Layout.Handle != 0)
                {
                    if (TryBeginDestroyDescriptorSetLayout(cached.Layout, "DescriptorLayoutCache.DestroyAll"))
                        Api!.DestroyDescriptorSetLayout(device, cached.Layout, null);
                }
            }

            _descriptorSetLayoutsByHash.Clear();
            _descriptorSetLayoutsByHandle.Clear();
        }
    }

    /// <summary>
    /// Builds the layout signature for a descriptor set layout based on the set index and bindings.
    /// </summary>
    /// <param name="setIndex">The index of the descriptor set.</param>
    /// <param name="bindings">The array of descriptor set layout bindings.</param>
    /// <returns>An array of descriptor layout binding signatures representing the layout signature.</returns>
    private static DescriptorLayoutBindingSignature[] BuildLayoutSignature(uint setIndex, DescriptorSetLayoutBinding[] bindings)
    {
        DescriptorLayoutBindingSignature[] signature = new DescriptorLayoutBindingSignature[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            DescriptorSetLayoutBinding binding = bindings[i];
            signature[i] = new DescriptorLayoutBindingSignature(
                setIndex,
                binding.Binding,
                binding.DescriptorType,
                binding.DescriptorCount,
                binding.StageFlags,
                VulkanBindlessMaterialDescriptors.IsBindlessTextureArrayBinding(setIndex, binding));
        }

        return signature;
    }

    /// <summary>
    /// Computes a hash value representing the layout schema for the given descriptor layout binding signature array.
    /// </summary>
    /// <param name="signature">The array of descriptor layout binding signatures to compute the hash for.</param>
    /// <returns>The computed hash value representing the layout schema.</returns>
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
            Mix(ref hash, part.Set);
            Mix(ref hash, (ulong)part.DescriptorType);
            Mix(ref hash, part.DescriptorCount);
            Mix(ref hash, (ulong)part.StageFlags);
            Mix(ref hash, part.VariableDescriptorCount ? 1ul : 0ul);
        }

        return hash;
    }

    /// <summary>
    /// Determines whether two descriptor layout binding signature arrays are equal.
    /// </summary>
    /// <param name="left">The first array of descriptor layout binding signatures to compare.</param>
    /// <param name="right">The second array of descriptor layout binding signatures to compare.</param>
    /// <returns>True if the two arrays are equal; otherwise, false.</returns>
    private static bool LayoutSignaturesEqual(DescriptorLayoutBindingSignature[] left, DescriptorLayoutBindingSignature[] right)
    {
        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
            if (left[i] != right[i])
                return false;

        return true;
    }

    /// <summary>
    /// Attempts to create a descriptor set layout for the specified set index and bindings, taking into account update-after-bind and variable descriptor count usage.
    /// </summary>
    /// <param name="setIndex">The index of the descriptor set to create the layout for.</param>
    /// <param name="bindings">The array of descriptor set layout bindings to include in the layout.</param>
    /// <param name="layout">The resulting descriptor set layout if creation is successful.</param>
    /// <param name="usesUpdateAfterBind">Indicates whether the created layout uses update-after-bind.</param>
    /// <param name="usesVariableDescriptorCount">Indicates whether the created layout uses variable descriptor count.</param>
    /// <returns>True if the descriptor set layout was successfully created; otherwise, false.</returns>
    private bool TryCreateDescriptorSetLayout(
        uint setIndex,
        DescriptorSetLayoutBinding[] bindings,
        out DescriptorSetLayout layout,
        out bool usesUpdateAfterBind,
        out bool usesVariableDescriptorCount)
    {
        layout = default;
        usesUpdateAfterBind = false;
        usesVariableDescriptorCount = false;

        DescriptorSetLayoutBinding[] layoutBindings = bindings.Length == 0
            ? Array.Empty<DescriptorSetLayoutBinding>()
            : [.. bindings];
        uint immutableSamplerCount = 0;
        for (int i = 0; i < layoutBindings.Length; i++)
            if (layoutBindings[i].DescriptorType == DescriptorType.Sampler &&
                TryGetCanonicalImmutableSampler(VulkanCanonicalSampler.LinearClamp, out _))
                immutableSamplerCount += Math.Max(layoutBindings[i].DescriptorCount, 1u);

        Sampler* immutableSamplers = immutableSamplerCount > 0
            ? (Sampler*)NativeMemory.Alloc((nuint)immutableSamplerCount, (nuint)sizeof(Sampler))
            : null;

        try
        {
            if (immutableSamplers is not null &&
                TryGetCanonicalImmutableSampler(VulkanCanonicalSampler.LinearClamp, out Sampler linearClampSampler))
            {
                int samplerOffset = 0;
                for (int i = 0; i < layoutBindings.Length; i++)
                {
                    if (layoutBindings[i].DescriptorType != DescriptorType.Sampler)
                        continue;

                    uint count = Math.Max(layoutBindings[i].DescriptorCount, 1u);
                    for (uint n = 0; n < count; n++)
                        immutableSamplers[samplerOffset + n] = linearClampSampler;

                    layoutBindings[i].PImmutableSamplers = immutableSamplers + samplerOffset;
                    samplerOffset += (int)count;
                }
            }

            DescriptorSetLayoutCreateFlags flags = 0;
            DescriptorSetLayoutBindingFlagsCreateInfo bindingFlagsInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo
            };

            DescriptorBindingFlags[]? bindingFlags = null;
            if (_supportsDescriptorIndexing && layoutBindings.Length > 0)
            {
                bindingFlags = new DescriptorBindingFlags[layoutBindings.Length];
                bool hasUpdateAfterBindBinding = false;
                for (int i = 0; i < layoutBindings.Length; i++)
                {
                    DescriptorBindingFlags flagsForBinding = 0;
                    bool isVariableDescriptorBinding = VulkanBindlessMaterialDescriptors.IsBindlessTextureArrayBinding(setIndex, layoutBindings[i]);

                    if (_supportsDescriptorBindingPartiallyBound)
                        flagsForBinding |= DescriptorBindingFlags.PartiallyBoundBit;

                    if (CanUseUpdateAfterBind(layoutBindings[i].DescriptorType))
                    {
                        flagsForBinding |= DescriptorBindingFlags.UpdateAfterBindBit;
                        hasUpdateAfterBindBinding = true;
                    }

                    if (isVariableDescriptorBinding)
                    {
                        flagsForBinding |= DescriptorBindingFlags.VariableDescriptorCountBit;
                        usesVariableDescriptorCount = true;
                    }

                    bindingFlags[i] = flagsForBinding;
                }

                usesUpdateAfterBind = hasUpdateAfterBindBinding;
                flags = hasUpdateAfterBindBinding
                    ? DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit
                    : 0;

                fixed (DescriptorBindingFlags* bindingFlagsPtr = bindingFlags)
                {
                    bindingFlagsInfo.BindingCount = (uint)bindingFlags.Length;
                    bindingFlagsInfo.PBindingFlags = bindingFlagsPtr;

                    fixed (DescriptorSetLayoutBinding* bindingsPtr = layoutBindings)
                    {
                        DescriptorSetLayoutCreateInfo layoutInfo = new()
                        {
                            SType = StructureType.DescriptorSetLayoutCreateInfo,
                            Flags = flags,
                            BindingCount = (uint)layoutBindings.Length,
                            PBindings = bindingsPtr,
                            PNext = &bindingFlagsInfo
                        };

                        if (Api!.CreateDescriptorSetLayout(device, ref layoutInfo, null, out layout) != Result.Success)
                            return false;
                    }
                }

                return true;
            }

            fixed (DescriptorSetLayoutBinding* bindingsPtr = layoutBindings)
            {
                DescriptorSetLayoutCreateInfo layoutInfo = new()
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = (uint)layoutBindings.Length,
                    PBindings = bindingsPtr,
                };

                if (Api!.CreateDescriptorSetLayout(device, ref layoutInfo, null, out layout) != Result.Success)
                    return false;
            }

            return true;
        }
        finally
        {
            if (immutableSamplers is not null)
                NativeMemory.Free(immutableSamplers);
        }
    }

    /// <summary>
    /// Determines whether the specified descriptor type can use the update-after-bind feature.
    /// </summary>
    /// <param name="descriptorType">The type of descriptor to check.</param>
    /// <returns>True if the descriptor type can use the update-after-bind feature; otherwise, false.</returns>
    private bool CanUseUpdateAfterBind(DescriptorType descriptorType)
    {
        if (!_supportsDescriptorBindingUpdateAfterBind)
            return false;

        return descriptorType switch
        {
            DescriptorType.SampledImage or DescriptorType.CombinedImageSampler or DescriptorType.Sampler => true,
            DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic => true,
            DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic => true,
            DescriptorType.StorageImage => _supportsDescriptorBindingStorageImageUpdateAfterBind,
            _ => false,
        };
    }
}
