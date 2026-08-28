using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>One stable canonical owner referenced by a resident draw artifact.</summary>
internal readonly record struct VulkanResidentDrawDependency(
    EBackendReadyCanonicalOwner Owner,
    AdvancedGpuHandle Handle);

/// <summary>Intrusive reverse-index links owned by one manifest entry.</summary>
internal struct VulkanResidentDrawDependencyLink
{
    internal VulkanResidentDrawTemplateHandle Previous;
    internal VulkanResidentDrawTemplateHandle Next;
    internal bool IsLinked;
}

/// <summary>
/// Immutable canonical and native dependency set captured when a resident draw
/// artifact is created. Canonical entries drive exact reverse invalidation;
/// native generations remain owned and pinned by the accompanying lease.
/// </summary>
internal sealed class VulkanResidentDrawDependencyManifest
{
    private readonly VulkanResidentDrawDependency[] _canonicalDependencies;
    private readonly VulkanResidentDrawDependencyLink[] _reverseLinks;

    private VulkanResidentDrawDependencyManifest(
        VulkanResidentDrawDependency[] canonicalDependencies)
    {
        _canonicalDependencies = canonicalDependencies;
        _reverseLinks = new VulkanResidentDrawDependencyLink[canonicalDependencies.Length];
    }

    internal ReadOnlySpan<VulkanResidentDrawDependency> CanonicalDependencies
        => _canonicalDependencies;

    internal Span<VulkanResidentDrawDependencyLink> ReverseLinks
        => _reverseLinks;

    internal static bool TryCreate(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw,
        out VulkanResidentDrawDependencyManifest? manifest)
    {
        manifest = null;
        AdvancedSharedGpuSceneDatabase? database = canonicalDraw.Database;
        AdvancedGpuHandle primary = canonicalDraw.Primary.Handle;
        if (database is null || !primary.IsValid ||
            !database.TryGetPublicationSnapshot(
                canonicalDraw.Publication,
                out AdvancedGpuScenePublicationSnapshot publication) ||
            publication.Draws.Sequence != canonicalDraw.Publication.Sequence ||
            publication.MaterialPayloads.Sequence != canonicalDraw.Publication.Sequence ||
            publication.ResourcePayloads.Sequence != canonicalDraw.Publication.Sequence ||
            !publication.ResourcePayloads.HasCompleteSourceImage ||
            !publication.Draws.TryGet(
                primary,
                out AdvancedDrawRecord draw) ||
            !publication.Materials.TryGet(
                draw.Material,
                out AdvancedMaterialRecord material))
        {
            return false;
        }

        Span<VulkanResidentDrawDependency> dependencies =
            stackalloc VulkanResidentDrawDependency[64];
        int count = 0;
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Instance, draw.Instance);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Geometry, draw.Geometry);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Material, draw.Material);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Deformation, draw.Deformation);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.RenderState, draw.RenderState);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.EditorIdentity, draw.EditorIdentity);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Transform, draw.CurrentTransform);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Transform, draw.PreviousTransform);

        AdvancedGpuHandle kernelHandle = new(
            material.ShadingKernelId,
            material.ShadingKernelGeneration);
        if (!publication.Kernels.TryGetDenseIndex(kernelHandle, out _))
            return false;
        AddUnique(
            dependencies,
            ref count,
            EBackendReadyCanonicalOwner.ShadingKernel,
            kernelHandle);

        if (!publication.MaterialPayloads.TryGetLayoutHandle(
                draw.Material,
                out AdvancedGpuHandle layoutHandle))
            return false;
        AddUnique(
            dependencies,
            ref count,
            EBackendReadyCanonicalOwner.MaterialLayout,
            layoutHandle);

        if (!publication.MaterialPayloads.TryGetTextureBindings(
                material,
                out ReadOnlySpan<AdvancedMaterialTextureBinding> bindings))
            return false;
        for (int bindingIndex = 0; bindingIndex < bindings.Length; ++bindingIndex)
        {
            ref readonly AdvancedMaterialTextureBinding binding =
                ref bindings[bindingIndex];
            if (binding.Texture.Handle.IsValid &&
                (!publication.Textures.TryGetDenseIndex(
                    binding.Texture.Handle,
                    out _) ||
                 !publication.ResourcePayloads.TryGetTextureSource(
                    binding.Texture.Handle,
                    out _)))
            {
                return false;
            }
            if (binding.Sampler.Handle.IsValid &&
                !publication.Samplers.TryGetDenseIndex(
                    binding.Sampler.Handle,
                    out _))
            {
                return false;
            }

            AddUnique(
                dependencies,
                ref count,
                EBackendReadyCanonicalOwner.Texture,
                binding.Texture.Handle);
            AddUnique(
                dependencies,
                ref count,
                EBackendReadyCanonicalOwner.Sampler,
                binding.Sampler.Handle);
        }

        // The intrusive reverse index is keyed by owner and stable slot. Two
        // generations of the same slot in one manifest would make its link
        // node ambiguous and must fail closed.
        for (int left = 0; left < count; ++left)
        for (int right = left + 1; right < count; ++right)
        {
            if (dependencies[left].Owner == dependencies[right].Owner &&
                dependencies[left].Handle.Index == dependencies[right].Handle.Index)
            {
                return false;
            }
        }

        manifest = new VulkanResidentDrawDependencyManifest(
            dependencies[..count].ToArray());
        return true;
    }

    private static void AddUnique(
        Span<VulkanResidentDrawDependency> dependencies,
        ref int count,
        EBackendReadyCanonicalOwner owner,
        AdvancedGpuHandle handle)
    {
        if (!handle.IsValid)
            return;

        for (int index = 0; index < count; ++index)
            if (dependencies[index].Owner == owner &&
                dependencies[index].Handle == handle)
            {
                return;
            }

        if ((uint)count >= (uint)dependencies.Length)
            throw new InvalidOperationException(
                "A resident draw exceeded the bounded canonical dependency manifest capacity.");
        dependencies[count++] = new VulkanResidentDrawDependency(owner, handle);
    }
}
