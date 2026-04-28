using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly object _descriptorUpdateTemplateCacheLock = new();
    private readonly Dictionary<ulong, List<CachedDescriptorUpdateTemplate>> _descriptorUpdateTemplateCache = new();

    private sealed class CachedDescriptorUpdateTemplate
    {
        public required DescriptorUpdateTemplate Template;
        public required DescriptorUpdateTemplateSignature[] Signature;
        public required ulong Hash;
    }

    private readonly record struct DescriptorUpdateTemplateSignature(
        ulong DescriptorSetLayout,
        ulong PipelineLayout,
        int BindPoint,
        uint SetIndex,
        uint DstBinding,
        uint DstArrayElement,
        uint DescriptorCount,
        DescriptorType DescriptorType,
        nuint Offset,
        nuint Stride);

    internal bool TryUpdateDescriptorSetWithTemplate(
        DescriptorSet descriptorSet,
        DescriptorSetLayout descriptorSetLayout,
        PipelineBindPoint bindPoint,
        PipelineLayout pipelineLayout,
        uint setIndex,
        ReadOnlySpan<WriteDescriptorSet> writes)
    {
        if (descriptorSet.Handle == 0 || descriptorSetLayout.Handle == 0 || writes.Length == 0)
            return false;

        DescriptorUpdateTemplateEntry[] entries = new DescriptorUpdateTemplateEntry[writes.Length];
        DescriptorUpdateTemplateSignature[] signature = new DescriptorUpdateTemplateSignature[writes.Length];
        nuint totalSize = 0;

        for (int i = 0; i < writes.Length; i++)
        {
            WriteDescriptorSet write = writes[i];
            if (write.DstSet.Handle != descriptorSet.Handle || write.DescriptorCount == 0)
                return false;

            nuint elementSize = GetDescriptorTemplateElementSize(write.DescriptorType);
            if (elementSize == 0 || !HasDescriptorTemplateSource(write))
                return false;

            nuint offset = AlignUp(totalSize, Math.Min(elementSize, (nuint)16));
            entries[i] = new DescriptorUpdateTemplateEntry
            {
                DstBinding = write.DstBinding,
                DstArrayElement = write.DstArrayElement,
                DescriptorCount = write.DescriptorCount,
                DescriptorType = write.DescriptorType,
                Offset = offset,
                Stride = elementSize
            };

            signature[i] = new DescriptorUpdateTemplateSignature(
                descriptorSetLayout.Handle,
                pipelineLayout.Handle,
                (int)bindPoint,
                setIndex,
                write.DstBinding,
                write.DstArrayElement,
                write.DescriptorCount,
                write.DescriptorType,
                offset,
                elementSize);

            totalSize = offset + elementSize * write.DescriptorCount;
        }

        if (totalSize == 0)
            return false;

        void* data = NativeMemory.Alloc(totalSize);
        try
        {
            for (int i = 0; i < writes.Length; i++)
                CopyDescriptorTemplateData(writes[i], (byte*)data + entries[i].Offset);

            if (!TryGetOrCreateDescriptorUpdateTemplate(
                descriptorSetLayout,
                bindPoint,
                pipelineLayout,
                setIndex,
                entries,
                signature,
                out DescriptorUpdateTemplate updateTemplate))
            {
                return false;
            }

            Api!.UpdateDescriptorSetWithTemplate(device, descriptorSet, updateTemplate, data);
            return true;
        }
        finally
        {
            NativeMemory.Free(data);
        }
    }

    private static bool HasDescriptorTemplateSource(in WriteDescriptorSet write)
        => write.DescriptorType switch
        {
            DescriptorType.UniformBuffer or DescriptorType.StorageBuffer or DescriptorType.UniformBufferDynamic or DescriptorType.StorageBufferDynamic
                => write.PBufferInfo is not null,
            DescriptorType.CombinedImageSampler or DescriptorType.Sampler or DescriptorType.SampledImage or DescriptorType.StorageImage or DescriptorType.InputAttachment
                => write.PImageInfo is not null,
            DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer
                => write.PTexelBufferView is not null,
            _ => false,
        };

    private bool TryGetOrCreateDescriptorUpdateTemplate(
        DescriptorSetLayout descriptorSetLayout,
        PipelineBindPoint bindPoint,
        PipelineLayout pipelineLayout,
        uint setIndex,
        DescriptorUpdateTemplateEntry[] entries,
        DescriptorUpdateTemplateSignature[] signature,
        out DescriptorUpdateTemplate updateTemplate)
    {
        updateTemplate = default;
        ulong hash = ComputeDescriptorUpdateTemplateHash(signature);

        lock (_descriptorUpdateTemplateCacheLock)
        {
            if (_descriptorUpdateTemplateCache.TryGetValue(hash, out List<CachedDescriptorUpdateTemplate>? bucket))
            {
                foreach (CachedDescriptorUpdateTemplate cached in bucket)
                {
                    if (!DescriptorUpdateTemplateSignaturesEqual(cached.Signature, signature))
                        continue;

                    updateTemplate = cached.Template;
                    return true;
                }
            }

            fixed (DescriptorUpdateTemplateEntry* entriesPtr = entries)
            {
                DescriptorUpdateTemplateCreateInfo createInfo = new()
                {
                    SType = StructureType.DescriptorUpdateTemplateCreateInfo,
                    DescriptorUpdateEntryCount = (uint)entries.Length,
                    PDescriptorUpdateEntries = entriesPtr,
                    TemplateType = DescriptorUpdateTemplateType.DescriptorSet,
                    DescriptorSetLayout = descriptorSetLayout,
                    PipelineBindPoint = bindPoint,
                    PipelineLayout = pipelineLayout,
                    Set = setIndex,
                };

                if (Api!.CreateDescriptorUpdateTemplate(device, &createInfo, null, out updateTemplate) != Result.Success)
                    return false;
            }

            CachedDescriptorUpdateTemplate created = new()
            {
                Template = updateTemplate,
                Signature = signature,
                Hash = hash,
            };

            bucket ??= [];
            bucket.Add(created);
            _descriptorUpdateTemplateCache[hash] = bucket;
            return true;
        }
    }

    private void DestroyDescriptorUpdateTemplateCache()
    {
        lock (_descriptorUpdateTemplateCacheLock)
        {
            foreach (List<CachedDescriptorUpdateTemplate> bucket in _descriptorUpdateTemplateCache.Values)
            {
                foreach (CachedDescriptorUpdateTemplate cached in bucket)
                {
                    if (cached.Template.Handle != 0)
                        Api!.DestroyDescriptorUpdateTemplate(device, cached.Template, null);
                }
            }

            _descriptorUpdateTemplateCache.Clear();
        }
    }

    private static ulong ComputeDescriptorUpdateTemplateHash(ReadOnlySpan<DescriptorUpdateTemplateSignature> signature)
    {
        ulong hash = 1469598103934665603UL;

        static void Mix(ref ulong value, ulong part)
        {
            value ^= part;
            value *= 1099511628211UL;
        }

        for (int i = 0; i < signature.Length; i++)
        {
            DescriptorUpdateTemplateSignature part = signature[i];
            Mix(ref hash, part.DescriptorSetLayout);
            Mix(ref hash, part.PipelineLayout);
            Mix(ref hash, unchecked((ulong)part.BindPoint));
            Mix(ref hash, part.SetIndex);
            Mix(ref hash, part.DstBinding);
            Mix(ref hash, part.DstArrayElement);
            Mix(ref hash, part.DescriptorCount);
            Mix(ref hash, (ulong)part.DescriptorType);
            Mix(ref hash, (ulong)part.Offset);
            Mix(ref hash, (ulong)part.Stride);
        }

        return hash;
    }

    private static bool DescriptorUpdateTemplateSignaturesEqual(
        DescriptorUpdateTemplateSignature[] left,
        DescriptorUpdateTemplateSignature[] right)
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

    private static nuint GetDescriptorTemplateElementSize(DescriptorType descriptorType)
        => descriptorType switch
        {
            DescriptorType.UniformBuffer or DescriptorType.StorageBuffer or DescriptorType.UniformBufferDynamic or DescriptorType.StorageBufferDynamic
                => (nuint)sizeof(DescriptorBufferInfo),
            DescriptorType.CombinedImageSampler or DescriptorType.Sampler or DescriptorType.SampledImage or DescriptorType.StorageImage or DescriptorType.InputAttachment
                => (nuint)sizeof(DescriptorImageInfo),
            DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer
                => (nuint)sizeof(BufferView),
            _ => 0,
        };

    private static void CopyDescriptorTemplateData(in WriteDescriptorSet write, void* destination)
    {
        nuint bytes = GetDescriptorTemplateElementSize(write.DescriptorType) * write.DescriptorCount;
        void* source = write.DescriptorType switch
        {
            DescriptorType.UniformBuffer or DescriptorType.StorageBuffer or DescriptorType.UniformBufferDynamic or DescriptorType.StorageBufferDynamic
                => write.PBufferInfo,
            DescriptorType.CombinedImageSampler or DescriptorType.Sampler or DescriptorType.SampledImage or DescriptorType.StorageImage or DescriptorType.InputAttachment
                => write.PImageInfo,
            DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer
                => write.PTexelBufferView,
            _ => null
        };

        if (source is not null && bytes > 0)
            System.Buffer.MemoryCopy(source, destination, bytes, bytes);
    }

    private static nuint AlignUp(nuint value, nuint alignment)
    {
        if (alignment <= 1)
            return value;

        nuint mask = alignment - 1;
        return (value + mask) & ~mask;
    }
}
