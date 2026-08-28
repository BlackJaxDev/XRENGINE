using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>One stable canonical owner referenced by a resident draw artifact.</summary>
internal readonly record struct VulkanResidentDrawDependency(
    EBackendReadyCanonicalOwner Owner,
    AdvancedGpuHandle Handle);

/// <summary>Intrusive reverse-index links owned by one manifest entry.</summary>
internal struct VulkanResidentDrawDependencyLink
{
    internal int PreviousPrimaryIndex;
    internal int NextPrimaryIndex;
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
            !database.TryCreateDrawDependencySnapshot(
                primary,
                out AdvancedSharedDrawDependencySnapshot snapshot) ||
            snapshot.Scene.Draw != primary ||
            !database.Materials.Materials.TryGet(
                snapshot.Scene.Material,
                out AdvancedMaterialRecord material))
        {
            return false;
        }

        Span<VulkanResidentDrawDependency> dependencies =
            stackalloc VulkanResidentDrawDependency[64];
        int count = 0;
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Instance, snapshot.Scene.Instance);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Geometry, snapshot.Scene.Geometry);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Material, snapshot.Scene.Material);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Deformation, snapshot.Scene.Deformation);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.RenderState, snapshot.Scene.RenderState);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.EditorIdentity, snapshot.Scene.EditorIdentity);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Transform, snapshot.Scene.CurrentTransform);
        AddUnique(dependencies, ref count, EBackendReadyCanonicalOwner.Transform, snapshot.Scene.PreviousTransform);

        AddUnique(
            dependencies,
            ref count,
            EBackendReadyCanonicalOwner.ShadingKernel,
            new AdvancedGpuHandle(
                material.ShadingKernelId,
                material.ShadingKernelGeneration));
        // Phase 2 consumes the packed-layout dependency when published, but
        // does not require the Phase 3 material-layout table to exist yet.
        if (database.Materials.TryGetLayoutHandle(
                snapshot.Scene.Material,
                out AdvancedGpuHandle layoutHandle))
        {
            AddUnique(
                dependencies,
                ref count,
                EBackendReadyCanonicalOwner.MaterialLayout,
                layoutHandle);
        }

        if (!database.Materials.TryGetTextureBindings(material, out var bindings))
            return false;
        for (int bindingIndex = 0; bindingIndex < bindings.Length; ++bindingIndex)
        {
            AddUnique(
                dependencies,
                ref count,
                EBackendReadyCanonicalOwner.Texture,
                bindings[bindingIndex].Texture.Handle);
            AddUnique(
                dependencies,
                ref count,
                EBackendReadyCanonicalOwner.Sampler,
                bindings[bindingIndex].Sampler.Handle);
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
