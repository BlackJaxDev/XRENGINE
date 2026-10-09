using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Resource declaration for one sealed bin. Resident-template manifests own
/// immutable arrays; canonical visibility manifests are views over
/// current-frame stream storage and are reset only when that stream thaws.
/// </summary>
internal sealed class VulkanBinResourceManifest
{
    private VulkanResidentDrawDependency[] _resources;
    private VulkanTemplateNativeResourceUse[] _nativeUses;
    private readonly bool _streamOwned;
    private int _resourceOffset;
    private int _resourceCapacity;
    private int _resourceCount;
    private int _nativeUseOffset;
    private int _nativeUseCapacity;
    private int _nativeUseCount;
    private readonly Dictionary<VulkanResidentDrawDependency, byte> _resourceLookup = [];
    private readonly Dictionary<(ObjectType Type, ulong Handle),
        (uint QueueFamily, ulong Generation, ImageLayout Layout)> _nativeHandleLookup = [];
    private readonly Dictionary<(ObjectType Type, ulong Handle, ulong Offset,
        ulong Length, uint Stride), int> _nativeRangeLookup = [];

    private VulkanBinResourceManifest(
        VulkanResidentDrawDependency[] resources,
        VulkanTemplateNativeResourceUse[] nativeUses,
        bool streamOwned)
    {
        _resources = resources;
        _nativeUses = nativeUses;
        _streamOwned = streamOwned;
        _resourceCapacity = resources.Length;
        _nativeUseCapacity = nativeUses.Length;
        if (!streamOwned)
        {
            _resourceCount = resources.Length;
            _nativeUseCount = nativeUses.Length;
        }
    }

    /// <summary>Creates one reusable view over stream-owned aggregate slabs.</summary>
    internal static VulkanBinResourceManifest CreateStreamOwned(
        VulkanResidentDrawDependency[] resources,
        VulkanTemplateNativeResourceUse[] nativeUses)
        => new(resources, nativeUses, streamOwned: true);

    /// <summary>
    /// Points a stream-owned view at its stream's grown aggregate slabs. Growth
    /// copies the old slab contents, so the bound offsets and counts stay valid.
    /// Streams grow only while they are mutable, never while a frozen reader
    /// can observe this view.
    /// </summary>
    internal void RebindStreamSlabs(
        VulkanResidentDrawDependency[] resources,
        VulkanTemplateNativeResourceUse[] nativeUses)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(nativeUses);
        if (!_streamOwned)
            throw new InvalidOperationException("Only stream-owned manifest views can be rebound.");
        if (resources.Length < _resources.Length || nativeUses.Length < _nativeUses.Length)
            throw new ArgumentException("A stream-owned manifest slab can only grow.");
        _resources = resources;
        _nativeUses = nativeUses;
    }
    internal ReadOnlySpan<VulkanResidentDrawDependency> Resources
        => _resources.AsSpan(_resourceOffset, _resourceCount);
    internal ReadOnlySpan<VulkanTemplateNativeResourceUse> NativeUses
        => _nativeUses.AsSpan(_nativeUseOffset, _nativeUseCount);
    internal int Count => _resourceCount;
    internal int NativeUseCount => _nativeUseCount;
    internal bool IsStreamOwned => _streamOwned;

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

        VulkanResidentDrawDependency[] resources = new VulkanResidentDrawDependency[resourceCapacity];
        VulkanTemplateNativeResourceUse[] nativeUses =
            new VulkanTemplateNativeResourceUse[nativeUseCapacity];
        VulkanBinResourceManifest result = new(resources, nativeUses, streamOwned: false);
        if (!result.TryMergeTemplates(
                templates,
                resources,
                nativeUses,
                out int resourceCount,
                out int nativeUseCount,
                out failure))
        {
            return false;
        }

        if (resourceCount != resources.Length)
            Array.Resize(ref result._resources, resourceCount);
        if (nativeUseCount != nativeUses.Length)
            Array.Resize(ref result._nativeUses, nativeUseCount);
        result._resourceCapacity = resourceCount;
        result._nativeUseCapacity = nativeUseCount;
        result._resourceCount = resourceCount;
        result._nativeUseCount = nativeUseCount;
        manifest = result;
        return true;
    }

    /// <summary>
    /// Rebuilds this stream-owned view in its exact non-overlapping slab range.
    /// </summary>
    internal bool TryResetFromTemplates(
        ReadOnlySpan<VulkanTemplateResourceManifest> templates,
        int resourceOffset,
        int resourceCapacity,
        int nativeUseOffset,
        int nativeUseCapacity,
        out VulkanBinResourceManifestFailure failure)
    {
        failure = VulkanBinResourceManifestFailure.None;
        if (!_streamOwned)
        {
            failure = VulkanBinResourceManifestFailure.InvalidCapacity;
            return false;
        }
        if (!TryBind(
                resourceOffset, resourceCapacity,
                nativeUseOffset, nativeUseCapacity,
                out failure))
        {
            return false;
        }

        return TryMergeTemplates(
            templates,
            _resources.AsSpan(_resourceOffset, _resourceCapacity),
            _nativeUses.AsSpan(_nativeUseOffset, _nativeUseCapacity),
            out _resourceCount,
            out _nativeUseCount,
            out failure);
    }

    /// <summary>Copies an aggregate manifest into this stream's owned slabs.</summary>
    internal bool TryCopyFrom(
        VulkanBinResourceManifest source,
        int resourceOffset,
        int resourceCapacity,
        int nativeUseOffset,
        int nativeUseCapacity,
        out VulkanBinResourceManifestFailure failure)
    {
        ArgumentNullException.ThrowIfNull(source);
        failure = VulkanBinResourceManifestFailure.None;
        if (!_streamOwned)
        {
            failure = VulkanBinResourceManifestFailure.InvalidCapacity;
            return false;
        }
        if (!TryBind(
                resourceOffset, resourceCapacity,
                nativeUseOffset, nativeUseCapacity,
                out failure))
        {
            return false;
        }
        if (source.Count > _resourceCapacity ||
            source.NativeUseCount > _nativeUseCapacity)
        {
            failure = VulkanBinResourceManifestFailure.CapacityExceeded;
            return false;
        }

        source.Resources.CopyTo(_resources.AsSpan(_resourceOffset, source.Count));
        source.NativeUses.CopyTo(_nativeUses.AsSpan(_nativeUseOffset, source.NativeUseCount));
        _resourceCount = source.Count;
        _nativeUseCount = source.NativeUseCount;
        return true;
    }

    private bool TryBind(
        int resourceOffset,
        int resourceCapacity,
        int nativeUseOffset,
        int nativeUseCapacity,
        out VulkanBinResourceManifestFailure failure)
    {
        failure = VulkanBinResourceManifestFailure.None;
        if (resourceOffset < 0 || resourceCapacity < 0 ||
            nativeUseOffset < 0 || nativeUseCapacity < 0 ||
            resourceOffset > _resources.Length - resourceCapacity ||
            nativeUseOffset > _nativeUses.Length - nativeUseCapacity)
        {
            failure = VulkanBinResourceManifestFailure.InvalidCapacity;
            return false;
        }

        _resourceOffset = resourceOffset;
        _resourceCapacity = resourceCapacity;
        _resourceCount = 0;
        _nativeUseOffset = nativeUseOffset;
        _nativeUseCapacity = nativeUseCapacity;
        _nativeUseCount = 0;
        return true;
    }

    private bool TryMergeTemplates(
        ReadOnlySpan<VulkanTemplateResourceManifest> templates,
        Span<VulkanResidentDrawDependency> resources,
        Span<VulkanTemplateNativeResourceUse> nativeUses,
        out int resourceCount,
        out int nativeUseCount,
        out VulkanBinResourceManifestFailure failure)
    {
        resourceCount = 0;
        nativeUseCount = 0;
        failure = VulkanBinResourceManifestFailure.None;
        _resourceLookup.Clear();
        _nativeHandleLookup.Clear();
        _nativeRangeLookup.Clear();
        _resourceLookup.EnsureCapacity(resources.Length);
        _nativeHandleLookup.EnsureCapacity(nativeUses.Length);
        _nativeRangeLookup.EnsureCapacity(nativeUses.Length);
        for (int templateIndex = 0; templateIndex < templates.Length; ++templateIndex)
        {
            ReadOnlySpan<VulkanResidentDrawDependency> templateResources =
                templates[templateIndex].Resources;
            for (int resourceIndex = 0; resourceIndex < templateResources.Length; ++resourceIndex)
            {
                VulkanResidentDrawDependency candidate = templateResources[resourceIndex];
                if (_resourceLookup.ContainsKey(candidate))
                    continue;
                if (resourceCount == resources.Length)
                {
                    failure = VulkanBinResourceManifestFailure.CapacityExceeded;
                    return false;
                }
                resources[resourceCount++] = candidate;
                _resourceLookup.Add(candidate, 0);
            }

            ReadOnlySpan<VulkanTemplateNativeResourceUse> templateNativeUses =
                templates[templateIndex].NativeUses;
            for (int nativeIndex = 0; nativeIndex < templateNativeUses.Length; ++nativeIndex)
            {
                VulkanTemplateNativeResourceUse candidate = templateNativeUses[nativeIndex];
                var handleKey = (candidate.ObjectType, candidate.Handle);
                if (_nativeHandleLookup.TryGetValue(handleKey, out var owner))
                {
                    if (owner.QueueFamily != candidate.QueueFamily)
                    {
                        failure = VulkanBinResourceManifestFailure.QueueFamilyConflict;
                        return false;
                    }
                    if (owner.Generation != candidate.NativeGeneration)
                    {
                        failure = VulkanBinResourceManifestFailure.NativeRangeConflict;
                        return false;
                    }
                    if (owner.Layout != candidate.RequiredLayout)
                    {
                        failure = VulkanBinResourceManifestFailure.ImageLayoutConflict;
                        return false;
                    }
                }
                else
                    _nativeHandleLookup.Add(handleKey,
                        (candidate.QueueFamily, candidate.NativeGeneration,
                            candidate.RequiredLayout));

                // The same buffer can own several distinct byte ranges.
                // Merge access only when the full range and stride match.
                var rangeKey = (candidate.ObjectType, candidate.Handle,
                    candidate.Offset, candidate.Length, candidate.ElementStride);
                if (_nativeRangeLookup.TryGetValue(rangeKey, out int existingIndex))
                {
                    VulkanTemplateNativeResourceUse existing = nativeUses[existingIndex];
                    nativeUses[existingIndex] = existing with
                    {
                        Access = existing.Access | candidate.Access,
                        Stages = existing.Stages | candidate.Stages,
                        AccessMask = existing.AccessMask | candidate.AccessMask,
                    };
                    continue;
                }
                if (nativeUseCount == nativeUses.Length)
                {
                    failure = VulkanBinResourceManifestFailure.CapacityExceeded;
                    return false;
                }
                nativeUses[nativeUseCount] = candidate;
                _nativeRangeLookup.Add(rangeKey, nativeUseCount++);
            }
        }
        return true;
    }
}
