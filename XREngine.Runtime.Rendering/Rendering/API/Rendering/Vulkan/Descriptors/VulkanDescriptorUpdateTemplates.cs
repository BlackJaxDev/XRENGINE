using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly object _descriptorUpdateTemplateCacheLock = new();
    private readonly Dictionary<ulong, List<CachedDescriptorUpdateTemplate>> _descriptorUpdateTemplateCache = new();

    /// <summary>
    /// Attempts to update a Vulkan descriptor set using a descriptor update template.
    /// </summary>
    /// <param name="descriptorSet">The Vulkan descriptor set to update.</param>
    /// <param name="descriptorSetLayout">The layout of the descriptor set.</param>
    /// <param name="bindPoint">The pipeline bind point (graphics or compute).</param>
    /// <param name="pipelineLayout">The pipeline layout associated with the descriptor set.</param>
    /// <param name="setIndex">The index of the descriptor set within the pipeline layout.</param>
    /// <param name="writes">The descriptor write operations to apply.</param>
    /// <returns>True if the descriptor set was successfully updated, false otherwise.</returns>
    internal bool TryUpdateDescriptorSetWithTemplate(
        DescriptorSet descriptorSet,
        DescriptorSetLayout descriptorSetLayout,
        PipelineBindPoint bindPoint,
        PipelineLayout pipelineLayout,
        uint setIndex,
        ReadOnlySpan<WriteDescriptorSet> writes)
    {
        if (!IsDeviceOperational)
            return false;

        if (descriptorSet.Handle == 0 || descriptorSetLayout.Handle == 0 || writes.Length == 0)
            return false;

        if (ContainsImageDescriptorWrites(writes))
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
                return false;

            if (!IsDeviceOperational)
                return false;

            Api!.UpdateDescriptorSetWithTemplate(device, descriptorSet, updateTemplate, data);
            return true;
        }
        finally
        {
            NativeMemory.Free(data);
        }
    }

    /// <summary>
    /// Determines whether a given descriptor write has a source suitable for a descriptor update template.
    /// </summary>
    /// <param name="write">The descriptor write to check.</param>
    /// <returns>True if the descriptor write has a source suitable for a descriptor update template; otherwise, false.</returns>
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

    /// <summary>
    /// Determines whether a collection of descriptor writes contains any image descriptor writes.
    /// </summary>
    /// <param name="writes">The collection of descriptor writes to check.</param>
    /// <returns>True if any of the descriptor writes are image descriptor writes; otherwise, false.</returns>
    private static bool ContainsImageDescriptorWrites(ReadOnlySpan<WriteDescriptorSet> writes)
    {
        for (int i = 0; i < writes.Length; i++)
            if (IsImageDescriptorType(writes[i].DescriptorType))
                return true;

        return false;
    }

    /// <summary>
    /// Determines whether a given descriptor type is an image descriptor type.
    /// </summary>
    /// <param name="descriptorType">The descriptor type to check.</param>
    /// <returns>True if the descriptor type is an image descriptor type; otherwise, false.</returns>
    private static bool IsImageDescriptorType(DescriptorType descriptorType)
        => descriptorType is DescriptorType.CombinedImageSampler
            or DescriptorType.Sampler
            or DescriptorType.SampledImage
            or DescriptorType.StorageImage
            or DescriptorType.InputAttachment;

    /// <summary>
    /// Tries to get an existing descriptor update template from the cache or creates a new one if it doesn't exist.
    /// </summary>
    /// <param name="descriptorSetLayout">The descriptor set layout associated with the update template.</param>
    /// <param name="bindPoint">The pipeline bind point (graphics or compute) for the update template.</param>
    /// <param name="pipelineLayout">The pipeline layout associated with the update template.</param>
    /// <param name="setIndex">The index of the descriptor set within the pipeline layout.</param>
    /// <param name="entries">The array of descriptor update template entries.</param>
    /// <param name="signature">The signature of the descriptor update template.</param>
    /// <param name="updateTemplate">The resulting descriptor update template if found or created.</param>
    /// <returns>True if an existing template was found or a new one was successfully created; otherwise, false.</returns>
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

    /// <summary>
    /// Destroys all cached descriptor update templates and clears the cache.
    /// This should be called during cleanup to ensure that all Vulkan resources are properly released.
    /// </summary>
    private void DestroyDescriptorUpdateTemplateCache()
    {
        lock (_descriptorUpdateTemplateCacheLock)
        {
            foreach (List<CachedDescriptorUpdateTemplate> bucket in _descriptorUpdateTemplateCache.Values)
                foreach (CachedDescriptorUpdateTemplate cached in bucket)
                    if (cached.Template.Handle != 0)
                        Api!.DestroyDescriptorUpdateTemplate(device, cached.Template, null);
            
            _descriptorUpdateTemplateCache.Clear();
        }
    }

    /// <summary>
    /// Computes a hash value for the given descriptor update template signature.
    /// </summary>
    /// <param name="signature">The descriptor update template signature to compute the hash for.</param>
    /// <returns>The computed hash value.</returns>
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

    /// <summary>
    /// Determines whether two descriptor update template signatures are equal.
    /// </summary>
    /// <param name="left">The first descriptor update template signature array to compare.</param>
    /// <param name="right">The second descriptor update template signature array to compare.</param>
    /// <returns>True if the two descriptor update template signatures are equal; otherwise, false.</returns>
    private static bool DescriptorUpdateTemplateSignaturesEqual(
        DescriptorUpdateTemplateSignature[] left,
        DescriptorUpdateTemplateSignature[] right)
    {
        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
            if (left[i] != right[i])
                return false;

        return true;
    }

    /// <summary>
    /// Gets the size of a descriptor template element based on its descriptor type.
    /// </summary>
    /// <param name="descriptorType">The descriptor type to get the element size for.</param>
    /// <returns>The size of the descriptor template element for the specified descriptor type.</returns>
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

    /// <summary>
    /// Copies the descriptor template data from the specified write descriptor set to the destination memory location.
    /// </summary>
    /// <param name="write">The write descriptor set containing the data to copy.</param>
    /// <param name="destination">The destination memory location to copy the data to.</param>
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

    /// <summary>
    /// Aligns the specified value up to the nearest multiple of the specified alignment.
    /// </summary>
    /// <param name="value">The value to align.</param>
    /// <param name="alignment">The alignment to align the value to.</param>
    /// <returns>The aligned value.</returns>
    private static nuint AlignUp(nuint value, nuint alignment)
    {
        if (alignment <= 1)
            return value;

        nuint mask = alignment - 1;
        return (value + mask) & ~mask;
    }
}
