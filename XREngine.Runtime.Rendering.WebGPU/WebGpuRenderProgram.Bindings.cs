using System.Text.Json;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    private readonly record struct SamplerSlots(int Texture, int Sampler);
    private readonly Dictionary<string, SamplerSlots> _samplers = new(StringComparer.Ordinal);
    private readonly List<WebGpuBindingSet> _bindingSets = new(8);
    private readonly Dictionary<(WebGpuOwnedStorageBuffer Owner, uint Index), WebGpuBindingSet> _ownedBindingSets = [];
    private WebGpuOwnedStorageBuffer? _bindingCacheOwner;
    private uint _bindingCacheIndex;
    private ulong _bindingCacheRevision;
    internal bool IsNativeRasterBindingPublication => _bindingCacheOwner is not null && Artifact.ComputeEntryPoint is null;
    private WebGpuBindingSet? _lastBindingSet;
    private int[] _resourceHandles = [];
    private uint[] _resourceSizes = [];
    private AbstractRenderAPIObject?[] _resourceOwners = [];
    private int _uniformArena;
    private ShaderTextureBindingType[] _textureShapes = [];

    private void ValidateResourceLayout(ShaderProgramArtifact artifact)
    {
        int uniforms = 0;
        SetField(ref _textureShapes, new ShaderTextureBindingType[artifact.Resources.Length]);
        for (int index = 0; index < artifact.Resources.Length; index++)
        {
            ShaderStageResourceLayout resource = artifact.Resources[index];
            if (resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer && resource.BindingType == "uniform" && resource.DynamicOffset)
            {
                uniforms++;
                continue;
            }
            if (resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer &&
                resource.BindingType is "read-only-storage" or "storage" && !resource.DynamicOffset && resource.Contract.ByteSize >= 4 &&
                (resource.BindingType != "storage" || (resource.Visibility & ShaderStageVisibility.Vertex) == 0))
                continue;
            if (resource.DynamicOffset) throw UnsupportedBinding(resource.Contract.Name, "textures and samplers cannot have dynamic offsets");
            bool texture = ShaderTextureBindingType.TryParse(resource.BindingType, out ShaderTextureBindingType shape);
            if (texture)
            {
                _textureShapes[index] = shape;
                if (resource.Contract.Kind != (shape.IsStorage ? ShaderAbiResourceKind.StorageImage : ShaderAbiResourceKind.SampledImage))
                    throw UnsupportedBinding(resource.Contract.Name, "the resource kind does not match its exact texture binding type");
                if (shape.IsStorage)
                {
                    if (shape.StorageAccess != "read-only" && (resource.Visibility & ShaderStageVisibility.Vertex) != 0)
                        throw UnsupportedBinding(resource.Contract.Name, "writable storage textures cannot be visible to vertex stages");
                    continue;
                }
            }
            else if (resource.Contract.Kind != ShaderAbiResourceKind.Sampler ||
                resource.BindingType is not ("filtering-sampler" or "non-filtering-sampler" or "comparison-sampler"))
                throw UnsupportedBinding(resource.Contract.Name, "the resource has no admitted exact texture or sampler shape");
            if (!_samplers.TryGetValue(resource.Contract.Name, out SamplerSlots slots)) slots = new(-1, -1);
            if (texture ? slots.Texture >= 0 : slots.Sampler >= 0)
                throw UnsupportedBinding(resource.Contract.Name, "a logical sampled resource may declare at most one texture and one sampler");
            _samplers[resource.Contract.Name] = texture ? slots with { Texture = index } : slots with { Sampler = index };
        }
        if (uniforms > 16) throw UnsupportedBinding(artifact.Name, "the program exceeds the bounded dynamic-uniform snapshot capacity");
        foreach ((string name, SamplerSlots slots) in _samplers)
        {
            if (slots.Texture < 0) throw UnsupportedBinding(name, "samplers require an exact logical-name texture binding");
            if (slots.Sampler < 0) continue; // Load-only textures have no sampler in their actual WGSL ABI.
            ShaderAbiResourceContract texture = artifact.Resources[slots.Texture].Contract;
            ShaderAbiResourceContract sampler = artifact.Resources[slots.Sampler].Contract;
            string sampleType = _textureShapes[slots.Texture].SampleType;
            string samplerType = artifact.Resources[slots.Sampler].BindingType;
            if (sampleType is "uint" or "sint" || (sampleType == "depth") != (samplerType == "comparison-sampler") ||
                sampleType == "unfilterable-float" && samplerType != "non-filtering-sampler")
                throw UnsupportedBinding(name, "the declared sampler type is incompatible with the exact texture sample type");
            if (texture.Owner != sampler.Owner || texture.Frequency != sampler.Frequency)
                throw UnsupportedBinding(name, "paired texture and sampler bindings must have the same owner and update frequency");
        }
        SetField(ref _resourceHandles, new int[artifact.Resources.Length]);
        SetField(ref _resourceSizes, new uint[artifact.Resources.Length]);
        SetField(ref _resourceOwners, new AbstractRenderAPIObject?[artifact.Resources.Length]);
    }

    internal bool IsWritableImage(int index) => Artifact.Resources[index].Contract.Kind == ShaderAbiResourceKind.StorageImage &&
        _textureShapes[index].StorageAccess != "read-only";

    private static void WriteBindingLayout(Utf8JsonWriter writer, ShaderStageResourceLayout resource)
    {
        if (resource.Contract.Kind is ShaderAbiResourceKind.UniformBuffer or ShaderAbiResourceKind.StorageBuffer)
        {
            writer.WriteStartObject("buffer");
            writer.WriteString("type", resource.BindingType);
            writer.WriteBoolean("hasDynamicOffset", resource.DynamicOffset);
            writer.WriteNumber("minBindingSize", resource.Contract.ByteSize);
        }
        else if (ShaderTextureBindingType.TryParse(resource.BindingType, out ShaderTextureBindingType texture))
        {
            writer.WriteStartObject(texture.IsStorage ? "storageTexture" : "texture");
            writer.WriteString("viewDimension", texture.ViewDimension);
            if (texture.IsStorage)
            {
                writer.WriteString("format", texture.StorageFormat);
                writer.WriteString("access", texture.StorageAccess);
            }
            else
            {
                writer.WriteString("sampleType", texture.SampleType);
                writer.WriteBoolean("multisampled", false);
            }
        }
        else
        {
            writer.WriteStartObject("sampler");
            writer.WriteString("type", resource.BindingType switch
            {
                "comparison-sampler" => "comparison", "non-filtering-sampler" => "non-filtering", _ => "filtering",
            });
        }
        writer.WriteEndObject();
    }

    /// <summary>Starts complete descriptor publication, preventing a prior draw's resources from filling missing bindings.</summary>
    internal void BeginResourceBindings()
    {
        if (!IsGenerated)
            throw new InvalidOperationException("WebGPU.Program.BindingsPending: prepare the program before publishing resources.");
        Array.Clear(_resourceHandles);
        Array.Clear(_resourceSizes);
        Array.Clear(_resourceOwners);
        SetField(ref _bindingCacheOwner, null, publishNotifications: false);
        SetField(ref _bindingCacheIndex, 0u, publishNotifications: false);
        SetField(ref _bindingCacheRevision, 0ul, publishNotifications: false);
    }

    /// <summary>Retains one current descriptor set for this completion-slot cohort, replacing only changed bindings.</summary>
    internal void SetNativeBindingCacheOwner(WebGpuOwnedStorageBuffer owner)
    {
        if (Artifact.ComputeEntryPoint is null || owner.Owner != Renderer || owner.IsRetired || !owner.IsGenerated)
            throw UnsupportedBinding(Artifact.Name, "native binding cache ownership requires live local compute storage");
        SetField(ref _bindingCacheOwner, owner, publishNotifications: false);
        SetField(ref _bindingCacheIndex, 0u, publishNotifications: false);
        SetField(ref _bindingCacheRevision, 0ul, publishNotifications: false);
    }

    /// <summary>Retains one exact coverage descriptor generation per native raster bucket and completion slot.</summary>
    internal void SetNativeRasterBindingCacheOwner(WebGpuOwnedStorageBuffer owner, uint bucket, ulong revision = 0)
    {
        if (Artifact.ComputeEntryPoint is not null || Artifact.VertexEntryPoint is null ||
            owner.Owner != Renderer || owner.IsRetired || !owner.IsGenerated || bucket >= WebGpuAdvancedVisibilityFrame.MaximumBuckets)
            throw UnsupportedBinding(Artifact.Name, "native raster cache ownership requires a bounded bucket in live local storage");
        SetField(ref _bindingCacheOwner, owner, publishNotifications: false);
        SetField(ref _bindingCacheIndex, bucket, publishNotifications: false);
        SetField(ref _bindingCacheRevision, revision, publishNotifications: false);
    }

    internal void ReleaseNativeBindingSetsAfter(WebGpuOwnedStorageBuffer owner, uint retainedBuckets)
    {
        for (int index = _bindingSets.Count - 1; index >= 0; index--)
            if (ReferenceEquals(_bindingSets[index].CacheOwner, owner) && _bindingSets[index].CacheIndex >= retainedBuckets)
                RemoveBindingSetAt(index);
    }

    /// <summary>Publishes a numeric storage binding against one unambiguous cooked program slot.</summary>
    private void SetBuffer(uint binding, XRDataBuffer buffer)
    {
        Generate();
        int matched = -1;
        for (int index = 0; index < Artifact.Resources.Length; index++)
        {
            ShaderStageResourceLayout resource = Artifact.Resources[index];
            if (resource.Contract.Kind != ShaderAbiResourceKind.StorageBuffer || resource.Contract.Binding != binding)
                continue;
            if (matched >= 0)
                throw UnsupportedBinding(resource.Contract.Name, "numeric storage binding is ambiguous across groups");
            matched = index;
        }
        if (matched < 0)
            throw UnsupportedBinding(Data.Name ?? "program", $"numeric storage binding {binding} is absent from the cooked layout");
        ShaderStageResourceLayout selected = Artifact.Resources[matched];
        if (Artifact.ComputeEntryPoint is null && selected.BindingType != "read-only-storage")
            throw UnsupportedBinding(selected.Contract.Name, "raster storage bindings must be explicitly read-only");
        if (buffer.Target is not (EBufferTarget.ShaderStorageBuffer or EBufferTarget.ArrayBuffer or
            EBufferTarget.ElementArrayBuffer or EBufferTarget.DrawIndirectBuffer or
            EBufferTarget.DispatchIndirectBuffer or EBufferTarget.ParameterBuffer) ||
            buffer.Length < selected.Contract.ByteSize ||
            selected.RuntimeArray && buffer.Length % selected.Contract.ByteSize != 0)
            throw UnsupportedBinding(selected.Contract.Name, "the buffer target or byte length is incompatible with its declared storage binding");
        WebGpuDataBuffer api = (WebGpuDataBuffer)Renderer.GetOrCreateAPIRenderObject(buffer, generateNow: true)!;
        api.Generate();
        if (!api.BackendIsReadyForGpuUse || api.ResourceHandle == 0)
            throw UnsupportedBinding(selected.Contract.Name, "the storage buffer is not ready for GPU use");
        api.StagePendingUpload();
        if (Renderer.DeviceCapabilities is not { } capabilities ||
            !capabilities.Limits.TryGetValue("maxStorageBufferBindingSize", out long limit) ||
            buffer.Length > (ulong)limit)
            throw UnsupportedBinding(selected.Contract.Name, "the logical storage binding exceeds the selected device's range limit");
        _resourceHandles[matched] = api.ResourceHandle;
        _resourceSizes[matched] = buffer.Length;
        _resourceOwners[matched] = api;
    }

    private void SetSampler(string name, IRenderTextureResource resource, int textureUnit)
    {
        if (!_samplers.TryGetValue(name, out SamplerSlots slots)) return;
        ShaderTextureBindingType shape = _textureShapes[slots.Texture];
        bool depth = shape.SampleType == "depth";
        (AbstractRenderAPIObject api, int view, int sampler) = (shape.ViewDimension, resource) switch
        {
            (_, XRTextureViewBase texture) => Bind((WebGpuTextureView)Renderer.GetOrCreateAPIRenderObject(texture)!, depth, slots.Sampler >= 0),
            ("2d", XRTexture2D texture) => Bind((WebGpuTexture2D)Renderer.GetOrCreateAPIRenderObject(texture)!, depth, slots.Sampler >= 0),
            ("2d-array", XRTexture2DArray texture) => Bind((WebGpuTexture2DArray)Renderer.GetOrCreateAPIRenderObject(texture)!, depth, slots.Sampler >= 0),
            ("cube", XRTextureCube texture) => Bind((WebGpuTextureCube)Renderer.GetOrCreateAPIRenderObject(texture)!, false, slots.Sampler >= 0),
            _ => throw UnsupportedBinding(name, $"the authored texture type does not match binding '{Artifact.Resources[slots.Texture].BindingType}'"),
        };
        _resourceHandles[slots.Texture] = view;
        _resourceOwners[slots.Texture] = api;
        if (slots.Sampler >= 0)
        {
            _resourceHandles[slots.Sampler] = sampler;
            _resourceOwners[slots.Sampler] = api;
        }
    }

    private static (AbstractRenderAPIObject Owner, int View, int Sampler) Bind(WebGpuTextureView texture, bool depth, bool sampled)
        => (texture, texture.GetSampledView(), sampled ? texture.GetSampler(depth) : 0);

    private static (AbstractRenderAPIObject Owner, int View, int Sampler) Bind(WebGpuTexture2D texture, bool depth, bool sampled)
        => (texture, texture.GetSampledView(depth), sampled ? texture.GetSampler(depth) : 0);

    private static (AbstractRenderAPIObject Owner, int View, int Sampler) Bind<T>(WebGpuLayeredTexture<T> texture,
        bool depth, bool sampled) where T : XRTexture
        => (texture, texture.GetSampledView(depth), sampled ? texture.GetSampler(depth) : 0);

    private void SetSamplerByLocation(int location, IRenderTextureResource texture, int textureUnit)
        => throw UnsupportedBinding(Data.Name ?? "program", "numeric sampler locations have no declared whole-program resource identity");

    /// <summary>Resolves only declared storage names against this mesh renderer's exact buffer keys.</summary>
    internal void PublishStorageBindings(XRMeshRenderer owner)
    {
        for (int index = 0; index < Artifact.Resources.Length; index++)
        {
            ShaderStageResourceLayout resource = Artifact.Resources[index];
            if (resource.Contract.Kind != ShaderAbiResourceKind.StorageBuffer)
                continue;
            string name = resource.Contract.Name;
            if (!owner.Buffers.TryGetValue(name, out XRDataBuffer? buffer) ||
                !string.Equals(buffer.AttributeName, name, StringComparison.Ordinal))
                throw UnsupportedBinding(name, "the renderer did not publish an exact logical-name XRDataBuffer");
            if (buffer.Target != EBufferTarget.ShaderStorageBuffer || buffer.Length < resource.Contract.ByteSize ||
                resource.RuntimeArray && buffer.Length % resource.Contract.ByteSize != 0)
                throw UnsupportedBinding(name, "the buffer target or byte length is incompatible with its declared storage binding");
            WebGpuDataBuffer api = (WebGpuDataBuffer)Renderer.GetOrCreateAPIRenderObject(buffer, generateNow: true)!;
            api.Generate();
            if (!api.BackendIsReadyForGpuUse || api.ResourceHandle == 0)
                throw UnsupportedBinding(name, "the owner-scoped storage buffer is not ready for GPU use");
            api.StagePendingUpload();
            if (Renderer.DeviceCapabilities is not { } capabilities ||
                !capabilities.Limits.TryGetValue("maxStorageBufferBindingSize", out long limit) ||
                buffer.Length > (ulong)limit)
                throw UnsupportedBinding(name, "the logical storage binding exceeds the selected device's range limit");
            _resourceHandles[index] = api.ResourceHandle;
            _resourceSizes[index] = buffer.Length;
            _resourceOwners[index] = api;
        }
    }

    internal bool TryGetStorageBinding(string name, out WebGpuDataBuffer? buffer)
    {
        for (int index = 0; index < Artifact.Resources.Length; index++)
            if (Artifact.Resources[index].Contract.Kind == ShaderAbiResourceKind.StorageBuffer &&
                string.Equals(Artifact.Resources[index].Contract.Name, name, StringComparison.Ordinal))
            {
                buffer = _resourceOwners[index] as WebGpuDataBuffer;
                return buffer is not null;
            }
        buffer = null;
        return false;
    }

    internal bool TrySnapshotBindings(bool deferMissingResources, out WebGpuBindingSet? bindings)
    {
        bindings = null;
        foreach ((string name, SamplerSlots slots) in _samplers)
        {
            if (_resourceHandles[slots.Texture] != 0 &&
                (slots.Sampler < 0 || _resourceHandles[slots.Sampler] != 0)) continue;
            if (deferMissingResources) return false;
            throw UnsupportedBinding(name, slots.Sampler < 0
                ? "the draw did not publish its required sampled texture"
                : "the draw did not publish its required texture and sampler");
        }
        for (int index = 0; index < Artifact.Resources.Length; index++)
            if (Artifact.Resources[index].Contract.Kind == ShaderAbiResourceKind.StorageBuffer &&
                (_resourceHandles[index] == 0 || _resourceOwners[index] is not (WebGpuDataBuffer or WebGpuOwnedStorageBuffer)))
                throw UnsupportedBinding(Artifact.Resources[index].Contract.Name, "the draw did not publish its required storage buffer");
        for (int index = 0; index < Artifact.Resources.Length; index++)
            if (Artifact.Resources[index].Contract.Kind == ShaderAbiResourceKind.StorageImage && _resourceHandles[index] == 0)
            {
                if (deferMissingResources) return false;
                throw UnsupportedBinding(Artifact.Resources[index].Contract.Name, "the dispatch did not publish its required storage image");
            }
        if (_bindingCacheOwner is { } cacheOwner)
        {
            if (_ownedBindingSets.TryGetValue((cacheOwner, _bindingCacheIndex), out WebGpuBindingSet? retained))
            {
                if (retained.CacheRevision == _bindingCacheRevision && retained.Matches(_resourceHandles, _resourceSizes))
                {
                    bindings = retained;
                    return true;
                }
                // A cohort's previous descriptor generation cannot be useful again.
                // Retire its command handles after recording, preserving earlier work.
                RemoveBindingSetAt(_bindingSets.IndexOf(retained));
            }
        }
        else
        {
            if (_lastBindingSet is { CacheOwner: null } && _lastBindingSet.Matches(_resourceHandles, _resourceSizes))
            {
                bindings = _lastBindingSet;
                return true;
            }
            foreach (WebGpuBindingSet candidate in _bindingSets)
            {
                if (candidate.CacheOwner is not null || !candidate.Matches(_resourceHandles, _resourceSizes)) continue;
                SetField(ref _lastBindingSet, candidate, publishNotifications: false);
                bindings = candidate;
                return true;
            }
            for (int index = _bindingSets.Count - 1; index >= 0; index--)
            {
                WebGpuBindingSet obsolete = _bindingSets[index];
                if (obsolete.CacheOwner is not null || !obsolete.SameResourcesWithDifferentSizes(_resourceHandles, _resourceSizes)) continue;
                RemoveBindingSetAt(index);
            }
        }
        int bindingCapacity = _bindingCacheOwner is not null ? (Artifact.ComputeEntryPoint is null ?
            WebGpuAdvancedVisibilityFrame.MaximumRetainedBuckets : WebGpuAdvancedShadingFrame.MaximumRetainedCohorts) :
            Artifact.ComputeEntryPoint is null ? 64 : 1024;
        if (_bindingSets.Count >= bindingCapacity)
            throw UnsupportedBinding(Artifact.Name, $"the program exceeds {bindingCapacity} retained resource binding sets; retire unused resources before publishing more");
        int[] groups = new int[_layouts.Length];
        try
        {
            for (int group = 0; group < groups.Length; group++)
                groups[group] = Renderer.CreateBindingGroup(DescribeGroup(Artifact, group, layout: false));
            bindings = new WebGpuBindingSet(Renderer, this, _resourceHandles, _resourceSizes, _resourceOwners, groups,
                _bindingCacheOwner, _bindingCacheIndex, _bindingCacheRevision);
            _bindingSets.Add(bindings);
            if (_bindingCacheOwner is not null) _ownedBindingSets.Add((_bindingCacheOwner, _bindingCacheIndex), bindings);
            SetField(ref _lastBindingSet, bindings, publishNotifications: false);
            return true;
        }
        catch
        {
            foreach (int group in groups)
                if (group != 0) Renderer.RetireEngineResourceAfterFrame(group);
            throw;
        }
    }

    internal void ReleaseBindingSetsUsing(AbstractRenderAPIObject resource)
    {
        ReleaseImageViewsUsing(resource);
        for (int index = _bindingSets.Count - 1; index >= 0; index--)
        {
            WebGpuBindingSet bindings = _bindingSets[index];
            if (!bindings.DependsOn(resource)) continue;
            RemoveBindingSetAt(index);
        }
    }

    internal void ReleaseBindingSetsUsingHandle(AbstractRenderAPIObject resource, int handle)
    {
        for (int index = _bindingSets.Count - 1; index >= 0; index--)
        {
            WebGpuBindingSet bindings = _bindingSets[index];
            if (!bindings.UsesHandle(resource, handle)) continue;
            RemoveBindingSetAt(index);
        }
    }

    private void ClearBindingSets()
    {
        foreach (WebGpuBindingSet bindings in _bindingSets) bindings.Dispose();
        _bindingSets.Clear();
        _ownedBindingSets.Clear();
        SetField(ref _bindingCacheOwner, null, publishNotifications: false);
        SetField(ref _lastBindingSet, null);
    }

    private void RemoveBindingSetAt(int index)
    {
        WebGpuBindingSet bindings = _bindingSets[index];
        bindings.Dispose();
        _bindingSets.RemoveAt(index);
        if (bindings.CacheOwner is not null) _ownedBindingSets.Remove((bindings.CacheOwner, bindings.CacheIndex));
        if (ReferenceEquals(bindings, _lastBindingSet)) SetField(ref _lastBindingSet, null);
    }

    private static NotSupportedException UnsupportedBinding(string name, string reason)
        => new($"WebGPU.Program.BindingUnsupported: '{name}': {reason}.");
}
