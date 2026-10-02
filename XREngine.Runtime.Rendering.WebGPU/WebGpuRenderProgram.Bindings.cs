using System.Text.Json;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    private readonly record struct SamplerSlots(int Texture, int Sampler);
    private readonly Dictionary<string, SamplerSlots> _samplers = new(StringComparer.Ordinal);
    private readonly List<WebGpuBindingSet> _bindingSets = new(8);
    private WebGpuBindingSet? _lastBindingSet;
    private int[] _resourceHandles = [];
    private uint[] _resourceSizes = [];
    private AbstractRenderAPIObject?[] _resourceOwners = [];
    private int _uniformArena;

    private void ValidateResourceLayout(ShaderProgramArtifact artifact)
    {
        int uniforms = 0;
        for (int index = 0; index < artifact.Resources.Length; index++)
        {
            ShaderStageResourceLayout resource = artifact.Resources[index];
            if (resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer &&
                resource.BindingType == "uniform" && resource.DynamicOffset)
            {
                uniforms++;
                continue;
            }
            if (resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer &&
                resource.BindingType is "read-only-storage" or "storage" && !resource.DynamicOffset &&
                resource.Contract.ByteSize >= 4 &&
                (resource.BindingType != "storage" || artifact.ComputeEntryPoint is not null))
                continue;
            if (resource.DynamicOffset || resource.BindingType is not ("texture-2d-float" or "texture-2d-array-float" or
                "texture-cube-float" or "texture-depth-2d" or "texture-depth-2d-array" or
                "filtering-sampler" or "comparison-sampler"))
                throw UnsupportedBinding(resource.Contract.Name, "only dynamic uniforms, admitted storage and exact typed sampled textures are admitted");
            bool texture = resource.Contract.Kind == ShaderAbiResourceKind.SampledImage;
            if (texture != (resource.BindingType is "texture-2d-float" or "texture-2d-array-float" or
                "texture-cube-float" or "texture-depth-2d" or "texture-depth-2d-array") ||
                !texture && resource.Contract.Kind != ShaderAbiResourceKind.Sampler)
                throw UnsupportedBinding(resource.Contract.Name, "the resource kind does not match its explicit binding type");
            if (!_samplers.TryGetValue(resource.Contract.Name, out SamplerSlots slots))
                slots = new(-1, -1);
            if (texture ? slots.Texture >= 0 : slots.Sampler >= 0)
                throw UnsupportedBinding(resource.Contract.Name, "a logical sampled resource may declare at most one texture and one sampler");
            _samplers[resource.Contract.Name] = texture ? slots with { Texture = index } : slots with { Sampler = index };
        }
        if (uniforms > 16)
            throw UnsupportedBinding(artifact.Name, "the program exceeds the bounded dynamic-uniform snapshot capacity");
        foreach ((string name, SamplerSlots slots) in _samplers)
        {
            if (slots.Texture < 0)
                throw UnsupportedBinding(name, "samplers require an exact logical-name texture binding");
            ShaderAbiResourceContract texture = artifact.Resources[slots.Texture].Contract;
            bool depth = artifact.Resources[slots.Texture].BindingType is "texture-depth-2d" or "texture-depth-2d-array";
            // Load-only depth resources intentionally have no sampler. Color textures and
            // comparison-sampled depth resources retain their exact paired ABI.
            if (slots.Sampler < 0)
            {
                if (!depth)
                    throw UnsupportedBinding(name, "float textures require an explicitly paired filtering sampler");
                continue;
            }
            ShaderAbiResourceContract sampler = artifact.Resources[slots.Sampler].Contract;
            bool comparison = artifact.Resources[slots.Sampler].BindingType == "comparison-sampler";
            if (depth != comparison)
                throw UnsupportedBinding(name, "depth textures require comparison samplers and float textures require filtering samplers");
            if (texture.Owner != sampler.Owner || texture.Frequency != sampler.Frequency)
                throw UnsupportedBinding(name, "paired texture and sampler bindings must have the same owner and update frequency");
        }
        SetField(ref _resourceHandles, new int[artifact.Resources.Length]);
        SetField(ref _resourceSizes, new uint[artifact.Resources.Length]);
        SetField(ref _resourceOwners, new AbstractRenderAPIObject?[artifact.Resources.Length]);
    }

    private static void WriteBindingLayout(Utf8JsonWriter writer, ShaderStageResourceLayout resource)
    {
        if (resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer)
        {
            writer.WriteStartObject("buffer");
            writer.WriteString("type", "uniform");
            writer.WriteBoolean("hasDynamicOffset", true);
            writer.WriteNumber("minBindingSize", resource.Contract.ByteSize);
        }
        else if (resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer)
        {
            writer.WriteStartObject("buffer");
            writer.WriteString("type", resource.BindingType);
            writer.WriteBoolean("hasDynamicOffset", false);
            writer.WriteNumber("minBindingSize", resource.Contract.ByteSize);
        }
        else if (resource.Contract.Kind == ShaderAbiResourceKind.SampledImage)
        {
            writer.WriteStartObject("texture");
            writer.WriteString("sampleType", resource.BindingType is "texture-depth-2d" or "texture-depth-2d-array" ? "depth" : "float");
            writer.WriteString("viewDimension", resource.BindingType switch
            {
                "texture-2d-array-float" or "texture-depth-2d-array" => "2d-array",
                "texture-cube-float" => "cube",
                _ => "2d",
            });
            writer.WriteBoolean("multisampled", false);
        }
        else
        {
            writer.WriteStartObject("sampler");
            writer.WriteString("type", resource.BindingType == "comparison-sampler" ? "comparison" : "filtering");
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
    }

    /// <summary>Publishes a numeric SSBO binding against one unambiguous cooked compute slot.</summary>
    private void SetBuffer(uint binding, XRDataBuffer buffer)
    {
        Generate();
        if (Artifact.ComputeEntryPoint is null)
            throw UnsupportedBinding(Data.Name ?? "program", "numeric storage bindings require a cooked compute entry point");
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
        if (buffer.Target is not (EBufferTarget.ShaderStorageBuffer or EBufferTarget.ArrayBuffer) ||
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
        // Shared publishers legitimately emit bindings absent from a particular shader variant.
        // Ignore those names before asking the renderer to create any resource wrapper.
        if (!_samplers.TryGetValue(name, out SamplerSlots slots)) return;
        string bindingType = Artifact.Resources[slots.Texture].BindingType;
        bool depth = bindingType is "texture-depth-2d" or "texture-depth-2d-array";
        (AbstractRenderAPIObject api, int view, int sampler) = (bindingType, resource) switch
        {
            ("texture-2d-float" or "texture-depth-2d", XRTexture2D texture) =>
                Bind((WebGpuTexture2D)Renderer.GetOrCreateAPIRenderObject(texture)!, depth, slots.Sampler >= 0),
            ("texture-2d-array-float" or "texture-depth-2d-array", XRTexture2DArray array) =>
                Bind((WebGpuTexture2DArray)Renderer.GetOrCreateAPIRenderObject(array)!, depth, slots.Sampler >= 0),
            ("texture-cube-float", XRTextureCube cube) =>
                Bind((WebGpuTextureCube)Renderer.GetOrCreateAPIRenderObject(cube)!, false, slots.Sampler >= 0),
            _ => throw UnsupportedBinding(name, $"the authored texture type does not match binding '{bindingType}'"),
        };
        _resourceHandles[slots.Texture] = view;
        _resourceOwners[slots.Texture] = api;
        if (slots.Sampler >= 0)
        {
            _resourceHandles[slots.Sampler] = sampler;
            _resourceOwners[slots.Sampler] = api;
        }
    }

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
                ? "the draw did not publish its required depth texture"
                : "the draw did not publish its required texture and sampler");
        }
        for (int index = 0; index < Artifact.Resources.Length; index++)
            if (Artifact.Resources[index].Contract.Kind == ShaderAbiResourceKind.StorageBuffer &&
                (_resourceHandles[index] == 0 || _resourceOwners[index] is not WebGpuDataBuffer))
                throw UnsupportedBinding(Artifact.Resources[index].Contract.Name, "the draw did not publish its required storage buffer");
        if (_lastBindingSet?.Matches(_resourceHandles, _resourceSizes) == true)
        {
            bindings = _lastBindingSet;
            return true;
        }
        foreach (WebGpuBindingSet candidate in _bindingSets)
        {
            if (!candidate.Matches(_resourceHandles, _resourceSizes)) continue;
            SetField(ref _lastBindingSet, candidate, publishNotifications: false);
            bindings = candidate;
            return true;
        }
        for (int index = _bindingSets.Count - 1; index >= 0; index--)
        {
            WebGpuBindingSet obsolete = _bindingSets[index];
            if (!obsolete.SameResourcesWithDifferentSizes(_resourceHandles, _resourceSizes)) continue;
            obsolete.Dispose();
            _bindingSets.RemoveAt(index);
            if (ReferenceEquals(obsolete, _lastBindingSet))
                SetField(ref _lastBindingSet, null);
        }
        int bindingCapacity = Artifact.ComputeEntryPoint is null ? 64 : 1024;
        if (_bindingSets.Count >= bindingCapacity)
            throw UnsupportedBinding(Artifact.Name, $"the program exceeds {bindingCapacity} retained resource binding sets; retire unused resources before publishing more");
        int[] groups = new int[_layouts.Length];
        try
        {
            for (int group = 0; group < groups.Length; group++)
                groups[group] = Renderer.CreateBindingGroup(DescribeGroup(Artifact, group, layout: false));
            bindings = new WebGpuBindingSet(Renderer, this, _resourceHandles, _resourceSizes, _resourceOwners, groups);
            _bindingSets.Add(bindings);
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
        for (int index = _bindingSets.Count - 1; index >= 0; index--)
        {
            WebGpuBindingSet bindings = _bindingSets[index];
            if (!bindings.DependsOn(resource)) continue;
            bindings.Dispose();
            _bindingSets.RemoveAt(index);
            if (ReferenceEquals(bindings, _lastBindingSet))
                SetField(ref _lastBindingSet, null);
        }
    }

    internal void ReleaseBindingSetsUsingHandle(AbstractRenderAPIObject resource, int handle)
    {
        for (int index = _bindingSets.Count - 1; index >= 0; index--)
        {
            WebGpuBindingSet bindings = _bindingSets[index];
            if (!bindings.UsesHandle(resource, handle)) continue;
            bindings.Dispose();
            _bindingSets.RemoveAt(index);
            if (ReferenceEquals(bindings, _lastBindingSet))
                SetField(ref _lastBindingSet, null);
        }
    }

    private void ClearBindingSets()
    {
        foreach (WebGpuBindingSet bindings in _bindingSets) bindings.Dispose();
        _bindingSets.Clear();
        SetField(ref _lastBindingSet, null);
    }

    private static NotSupportedException UnsupportedBinding(string name, string reason)
        => new($"WebGPU.Program.BindingUnsupported: '{name}': {reason}.");
}
