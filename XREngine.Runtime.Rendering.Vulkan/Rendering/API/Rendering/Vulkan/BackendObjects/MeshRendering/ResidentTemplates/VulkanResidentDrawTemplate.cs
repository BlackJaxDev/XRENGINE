namespace XREngine.Rendering.Vulkan;

/// <summary>One bounded resident Vulkan draw-template entry and its lease.</summary>
internal sealed class VulkanResidentDrawTemplate : IDisposable
{
    private VulkanResidentTemplateDependencyLease? _dependencyLease;
    private int _detached;
    private int _useCount;

    internal VulkanResidentDrawTemplate(
        in VulkanResidentDrawTemplateStructuralIdentity structuralIdentity,
        in VulkanResidentDrawTemplateVariantKey variant,
        in VulkanResidentDrawTemplateGenerationDomains generations,
        in VulkanResidentDrawTemplateNativeState nativeState,
        VulkanResidentDrawDependencyManifest dependencyManifest,
        VulkanResidentTemplateDependencyLease dependencyLease)
    {
        StructuralIdentity = structuralIdentity;
        Variant = variant;
        Generations = generations;
        NativeState = nativeState;
        DependencyManifest = dependencyManifest;
        ResourceManifest = VulkanTemplateResourceManifest.Create(
            dependencyManifest,
            nativeState);
        RenderBinKey = VulkanRenderBinKey.Create(
            structuralIdentity,
            variant,
            nativeState);
        _dependencyLease = dependencyLease;
    }

    internal VulkanResidentDrawTemplateStructuralIdentity StructuralIdentity { get; }
    internal VulkanResidentDrawTemplateVariantKey Variant { get; }
    internal VulkanResidentDrawTemplateGenerationDomains Generations { get; private set; }
    internal VulkanResidentDrawTemplateNativeState NativeState { get; }
    internal VulkanResidentDrawDependencyManifest DependencyManifest { get; }
    internal VulkanTemplateResourceManifest ResourceManifest { get; }
    internal VulkanRenderBinKey RenderBinKey { get; }
    /// <summary>
    /// Only commutative opaque vertex-input work may enter a stable bin. Ordered,
    /// transparent, shadow, and non-native dialect work stays on the ordinary
    /// prepared stream until it has an explicit lane contract.
    /// </summary>
    internal bool IsStableBinEligible
        => !NativeState.DrawTemplate.BlendEnabled &&
           !NativeState.DrawTemplate.ShadowUniformState.IsShadowPass &&
           Variant.MeshDialect == EVulkanResidentTemplateMeshDialect.VertexInput;
    internal VulkanResidentTemplateDependencyLease? DependencyLease => _dependencyLease;
    internal ulong NativeDependencyIdentity { get; set; }
    internal VulkanNativeDependencyHandle NativeDependencyHandle { get; set; }

    internal void AdvanceDataContent(ulong dataContentGeneration)
        => Generations = Generations with { DataContent = dataContentGeneration };

    /// <summary>Acquires one prepared/submitted use while table ownership is live.</summary>
    internal bool TryAcquireUse()
    {
        if (Volatile.Read(ref _detached) != 0)
            return false;

        Interlocked.Increment(ref _useCount);
        if (Volatile.Read(ref _detached) == 0)
            return true;

        ReleaseUse();
        return false;
    }

    internal void ReleaseUse()
    {
        int remaining = Interlocked.Decrement(ref _useCount);
        if (remaining < 0)
            throw new InvalidOperationException("Resident draw-template use ownership underflowed.");
        if (remaining == 0 && Volatile.Read(ref _detached) != 0)
            ReleaseDependencies();
    }

    /// <summary>
    /// Removes table ownership immediately. Native dependencies remain retained
    /// until every prepared or submitted use has retired.
    /// </summary>
    internal void Detach()
    {
        if (Interlocked.Exchange(ref _detached, 1) != 0)
            return;
        if (Volatile.Read(ref _useCount) == 0)
            ReleaseDependencies();
    }

    public void Dispose()
        => Detach();

    private void ReleaseDependencies()
        => Interlocked.Exchange(ref _dependencyLease, null)?.Dispose();
}
