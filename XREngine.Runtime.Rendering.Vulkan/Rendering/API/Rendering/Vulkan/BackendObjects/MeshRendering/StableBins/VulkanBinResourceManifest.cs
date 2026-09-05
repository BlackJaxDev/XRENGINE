namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable resource declaration for one sealed bin. Creation is a cold
/// topology operation and fails rather than dropping resources on overflow.
/// </summary>
internal sealed class VulkanBinResourceManifest
{
    private readonly VulkanResidentDrawDependency[] _resources;
    private readonly VulkanTemplateNativeResourceUse[] _nativeUses;

    private VulkanBinResourceManifest(
        VulkanResidentDrawDependency[] resources,
        VulkanTemplateNativeResourceUse[] nativeUses)
    {
        _resources = resources;
        _nativeUses = nativeUses;
    }

    internal ReadOnlySpan<VulkanResidentDrawDependency> Resources => _resources;
    internal ReadOnlySpan<VulkanTemplateNativeResourceUse> NativeUses => _nativeUses;
    internal int Count => _resources.Length;

    internal static bool TryCreate(
        ReadOnlySpan<VulkanTemplateResourceManifest> templates,
        int resourceCapacity,
        int nativeUseCapacity,
        out VulkanBinResourceManifest? manifest,
        out VulkanBinResourceManifestFailure failure)
    {
        manifest = null;
        failure = VulkanBinResourceManifestFailure.None;
        if (resourceCapacity < 0 || nativeUseCapacity < 0)
        {
            failure = VulkanBinResourceManifestFailure.InvalidCapacity;
            return false;
        }

        VulkanResidentDrawDependency[] unique = new VulkanResidentDrawDependency[resourceCapacity];
        VulkanTemplateNativeResourceUse[] nativeUses =
            new VulkanTemplateNativeResourceUse[nativeUseCapacity];
        int count = 0;
        int nativeCount = 0;
        for (int templateIndex = 0; templateIndex < templates.Length; ++templateIndex)
        {
            ReadOnlySpan<VulkanResidentDrawDependency> resources =
                templates[templateIndex].Resources;
            for (int resourceIndex = 0; resourceIndex < resources.Length; ++resourceIndex)
            {
                VulkanResidentDrawDependency candidate = resources[resourceIndex];
                bool exists = false;
                for (int existingIndex = 0; existingIndex < count; ++existingIndex)
                {
                    if (unique[existingIndex] == candidate)
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists)
                    continue;
                if (count == unique.Length)
                {
                    failure = VulkanBinResourceManifestFailure.CapacityExceeded;
                    return false;
                }
                unique[count++] = candidate;
            }

            ReadOnlySpan<VulkanTemplateNativeResourceUse> templateNativeUses =
                templates[templateIndex].NativeUses;
            for (int nativeIndex = 0; nativeIndex < templateNativeUses.Length; ++nativeIndex)
            {
                VulkanTemplateNativeResourceUse candidate = templateNativeUses[nativeIndex];
                bool exists = false;
                for (int existingIndex = 0; existingIndex < nativeCount; ++existingIndex)
                {
                    ref readonly VulkanTemplateNativeResourceUse existing =
                        ref nativeUses[existingIndex];
                    if (existing.ObjectType != candidate.ObjectType ||
                        existing.Handle != candidate.Handle)
                        continue;

                    // A sealed bin cannot contain incompatible ownership or
                    // layout requirements for a single native resource.
                    if (existing.QueueFamily != candidate.QueueFamily)
                    {
                        failure = VulkanBinResourceManifestFailure.QueueFamilyConflict;
                        return false;
                    }
                    if (existing.NativeGeneration != candidate.NativeGeneration)
                    {
                        failure = VulkanBinResourceManifestFailure.NativeRangeConflict;
                        return false;
                    }
                    if (existing.RequiredLayout != candidate.RequiredLayout)
                    {
                        failure = VulkanBinResourceManifestFailure.ImageLayoutConflict;
                        return false;
                    }
                    // A packed arena deliberately stores vertex and index
                    // columns in distinct ranges of one VkBuffer. Keep each
                    // exact range in the manifest instead of treating shared
                    // allocation identity as conflicting range ownership.
                    if (existing.Offset != candidate.Offset ||
                        existing.Length != candidate.Length ||
                        existing.ElementStride != candidate.ElementStride)
                        continue;
                    {
                        nativeUses[existingIndex] = existing with
                        {
                            Access = existing.Access | candidate.Access,
                            Stages = existing.Stages | candidate.Stages,
                            AccessMask = existing.AccessMask | candidate.AccessMask,
                        };
                        exists = true;
                        break;
                    }
                }
                if (exists)
                    continue;
                if (nativeCount == nativeUses.Length)
                {
                    failure = VulkanBinResourceManifestFailure.CapacityExceeded;
                    return false;
                }
                nativeUses[nativeCount++] = candidate;
            }
        }

        if (count != unique.Length)
            Array.Resize(ref unique, count);
        if (nativeCount != nativeUses.Length)
            Array.Resize(ref nativeUses, nativeCount);
        manifest = new(unique, nativeUses);
        return true;
    }
}
