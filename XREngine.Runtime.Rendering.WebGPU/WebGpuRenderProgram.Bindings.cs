using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    private readonly record struct SamplerSlots(int Texture, int Sampler);
    private readonly Dictionary<string, SamplerSlots> _samplers = new(StringComparer.Ordinal);
    private readonly List<WebGpuBindingSet> _bindingSets = new(8);
    private WebGpuBindingSet? _lastBindingSet;
    private int[] _resourceHandles = [];
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
            if (resource.DynamicOffset || resource.BindingType is not ("texture-2d-float" or "filtering-sampler" or "texture-depth-2d" or "comparison-sampler"))
                throw UnsupportedBinding(resource.Contract.Name, "only dynamic uniforms and exact 2D float/filtering or depth/comparison sampler pairs are admitted");
            bool texture = resource.Contract.Kind == ShaderAbiResourceKind.SampledImage;
            if (texture != (resource.BindingType is "texture-2d-float" or "texture-depth-2d") ||
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
            if (slots.Texture < 0 || slots.Sampler < 0)
                throw UnsupportedBinding(name, "texture and sampler bindings must explicitly share the same logical resource name");
            ShaderAbiResourceContract texture = artifact.Resources[slots.Texture].Contract;
            ShaderAbiResourceContract sampler = artifact.Resources[slots.Sampler].Contract;
            bool depth = artifact.Resources[slots.Texture].BindingType == "texture-depth-2d";
            bool comparison = artifact.Resources[slots.Sampler].BindingType == "comparison-sampler";
            if (depth != comparison)
                throw UnsupportedBinding(name, "depth textures require comparison samplers and float textures require filtering samplers");
            if (texture.Owner != sampler.Owner || texture.Frequency != sampler.Frequency)
                throw UnsupportedBinding(name, "paired texture and sampler bindings must have the same owner and update frequency");
        }
        SetField(ref _resourceHandles, new int[artifact.Resources.Length]);
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
        else if (resource.Contract.Kind == ShaderAbiResourceKind.SampledImage)
        {
            writer.WriteStartObject("texture");
            writer.WriteString("sampleType", resource.BindingType == "texture-depth-2d" ? "depth" : "float");
            writer.WriteString("viewDimension", "2d");
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
        Array.Clear(_resourceOwners);
    }

    private void SetSampler(string name, IRenderTextureResource resource, int textureUnit)
    {
        // Shared publishers legitimately emit bindings absent from a particular shader variant.
        // Ignore those names before asking the renderer to create any resource wrapper.
        if (!_samplers.TryGetValue(name, out SamplerSlots slots)) return;
        if (resource is not XRTexture2D texture)
            throw UnsupportedBinding(name, "the selected binding requires an authored XRTexture2D");
        WebGpuTexture2D api = (WebGpuTexture2D)Renderer.GetOrCreateAPIRenderObject(texture)!;
        bool depth = Artifact.Resources[slots.Texture].BindingType == "texture-depth-2d";
        int view = api.GetSampledView(depth);
        int sampler = api.GetSampler(depth);
        _resourceHandles[slots.Texture] = view;
        _resourceHandles[slots.Sampler] = sampler;
        _resourceOwners[slots.Texture] = api;
        _resourceOwners[slots.Sampler] = api;
    }

    private void SetSamplerByLocation(int location, IRenderTextureResource texture, int textureUnit)
        => throw UnsupportedBinding(Data.Name ?? "program", "numeric sampler locations have no declared whole-program resource identity");

    internal bool TrySnapshotBindings(bool deferMissingResources, out WebGpuBindingSet? bindings)
    {
        bindings = null;
        foreach ((string name, SamplerSlots slots) in _samplers)
        {
            if (_resourceHandles[slots.Texture] != 0 && _resourceHandles[slots.Sampler] != 0) continue;
            if (deferMissingResources) return false;
            throw UnsupportedBinding(name, "the draw did not publish its required texture and sampler");
        }
        if (_lastBindingSet?.Matches(_resourceHandles) == true)
        {
            bindings = _lastBindingSet;
            return true;
        }
        foreach (WebGpuBindingSet candidate in _bindingSets)
        {
            if (!candidate.Matches(_resourceHandles)) continue;
            SetField(ref _lastBindingSet, candidate, publishNotifications: false);
            bindings = candidate;
            return true;
        }
        if (_bindingSets.Count >= 64)
            throw UnsupportedBinding(Artifact.Name, "the program exceeds 64 retained resource binding sets; retire unused resources before publishing more");
        int[] groups = new int[_layouts.Length];
        try
        {
            for (int group = 0; group < groups.Length; group++)
                groups[group] = Renderer.CreateBindingGroup(DescribeGroup(Artifact, group, layout: false));
            bindings = new WebGpuBindingSet(Renderer, _resourceHandles, _resourceOwners, groups);
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

    private void ClearBindingSets()
    {
        foreach (WebGpuBindingSet bindings in _bindingSets) bindings.Dispose();
        _bindingSets.Clear();
        SetField(ref _lastBindingSet, null);
    }

    private static NotSupportedException UnsupportedBinding(string name, string reason)
        => new($"WebGPU.Program.BindingUnsupported: '{name}': {reason}.");
}
