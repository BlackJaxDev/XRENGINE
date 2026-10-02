using System.Buffers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Compiles an explicit cooked engine module and retains its immutable resource binding layouts.</summary>
public sealed partial class WebGpuRenderProgram : WebGpuObject<XRRenderProgram>, IRenderPreparationState
{
    private readonly record struct UniformTarget(WebGpuUniformBlock Block, ShaderAbiMemberContract Member);
    private readonly Dictionary<string, List<UniformTarget>> _uniforms = new(StringComparer.Ordinal);
    private ShaderProgramArtifact? _artifact;
    private Task? _preparation;
    private int _shaderHandle;
    private int[] _layouts = [];
    private WebGpuUniformBlock[] _blocks = [];
    private int _preparationEpoch;

    public WebGpuRenderProgram(WebGpuRendererHost renderer, XRRenderProgram data) : base(renderer, data)
    {
        data.LinkRequested += Link;
        data.UseRequested += Link;
        data.UniformSetMatrix4x4Requested += SetMatrix;
        data.UniformSetFloatRequested += SetFloat;
        data.UniformSetVector2Requested += SetVector2;
        data.UniformSetVector3Requested += SetVector3;
        data.UniformSetVector4Requested += SetVector4;
        data.UniformSetIntRequested += SetInt;
        data.UniformSetUIntRequested += SetUInt;
        data.UniformSetBoolRequested += SetBool;
        data.SamplerRequested += SetSampler;
        data.SamplerRequestedByLocation += SetSamplerByLocation;
        data.BindBufferRequested += SetBuffer;
    }

    public ShaderProgramArtifact Artifact => _artifact
        ?? throw new InvalidOperationException("WebGPU.Program.Pending: no cooked program has been prepared.");
    public int ShaderHandle => _shaderHandle;
    public ReadOnlySpan<int> LayoutHandles => _layouts;
    public int UniformBlockCount => _blocks.Length;
    internal bool RequiresCameraUniforms => _uniforms.ContainsKey("ViewProjection") || _uniforms.ContainsKey("CameraPosition") ||
        _uniforms.ContainsKey("InverseViewMatrix") || _uniforms.ContainsKey("InverseProjMatrix");
    public override bool IsGenerated => _shaderHandle != 0 && _preparation?.IsCompletedSuccessfully == true;
    public bool IsPreparedForRendering => IsGenerated;

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (!Data.TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, Renderer.ShaderArtifacts, out ShaderProgramArtifact? artifact) || artifact is null)
            throw new NotSupportedException($"WebGPU.Program.ArtifactMissing: program '{Data.Name}' has no complete WebGPU shader companion.");
        if (_artifact is not null && _artifact.Identity != artifact.Identity)
            throw new NotSupportedException("WebGPU.Program.ArtifactChanged: replace the program and dependent mesh bindings at a resource-generation boundary.");
        if (_preparation is null)
        {
            SetField(ref _artifact, artifact);
            SetField(ref _preparation, PrepareAsync(artifact, _preparationEpoch));
        }
        Task preparation = _preparation
            ?? throw new InvalidOperationException("WebGPU.Program.PreparationMissing: the module preparation request was not retained.");
        if (preparation.IsFaulted || preparation.IsCanceled)
            preparation.GetAwaiter().GetResult();
    }

    public bool TryPrepareForRendering()
    {
        Generate();
        return IsGenerated;
    }

    private async Task PrepareAsync(ShaderProgramArtifact artifact, int epoch)
    {
        ValidateResourceLayout(artifact);
        InitializeUniformBlocks(artifact);
        int shader = await Renderer.CreateShaderModuleAsync(artifact.Artifact.WgslSource, artifact.Name);
        if (IsRetired || Data.IsDestroyed || !Renderer.AcceptsBackendWork || epoch != _preparationEpoch)
        {
            if (Renderer.State == BrowserRendererState.Ready)
                Renderer.RetireEngineResource(shader);
            throw new InvalidOperationException("WebGPU.Program.Obsolete: module preparation completed after its engine owner retired.");
        }
        SetField(ref _shaderHandle, shader);

        int groupCount = 0;
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
            groupCount = Math.Max(groupCount, checked((int)resource.Contract.Set + 1));
        bool hasUniforms = false;
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
            hasUniforms |= resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer;
        SetField(ref _uniformArena, hasUniforms ? Renderer.EnsureEngineUniformBuffer() : 0);
        SetField(ref _layouts, new int[groupCount]);
        for (int group = 0; group < groupCount; group++)
            _layouts[group] = Renderer.CreateBindingLayout(DescribeGroup(artifact, group, layout: true));
        Data.SetBackendLinked(true);
    }

    /// <summary>Installs CPU uniform targets before asynchronous module compilation can defer a frame.</summary>
    private void InitializeUniformBlocks(ShaderProgramArtifact artifact)
    {
        int groupCount = 0;
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
            groupCount = Math.Max(groupCount, checked((int)resource.Contract.Set + 1));
        List<WebGpuUniformBlock> blocks = [];
        for (int group = 0; group < groupCount; group++)
        {
            foreach (ShaderStageResourceLayout resource in artifact.Resources.OrderBy(static resource => resource.Contract.Binding))
            {
                if (resource.Contract.Set != group || resource.Contract.Kind != ShaderAbiResourceKind.UniformBuffer)
                    continue;
                WebGpuUniformBlock block = new(resource.Contract);
                blocks.Add(block);
                foreach (ShaderAbiMemberContract member in resource.Contract.Members)
                {
                    if (!_uniforms.TryGetValue(member.ProviderName, out List<UniformTarget>? targets))
                        _uniforms.Add(member.ProviderName, targets = []);
                    targets.Add(new(block, member));
                }
            }
        }
        SetField(ref _blocks, blocks.ToArray());
    }

    private string DescribeGroup(ShaderProgramArtifact artifact, int group, bool layout)
    {
        ArrayBufferWriter<byte> output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WriteString("label", artifact.Name);
            if (!layout) writer.WriteNumber("layout", _layouts[group]);
            writer.WriteStartArray("entries");
            for (int index = 0; index < artifact.Resources.Length; index++)
            {
                ShaderStageResourceLayout resource = artifact.Resources[index];
                if (resource.Contract.Set != group) continue;
                writer.WriteStartObject();
                writer.WriteNumber("binding", resource.Contract.Binding);
                if (layout)
                {
                    writer.WriteNumber("visibility", (int)resource.Visibility);
                    WriteBindingLayout(writer, resource);
                }
                else if (resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer)
                {
                    writer.WriteNumber("resource", _uniformArena);
                    writer.WriteNumber("offset", 0);
                    writer.WriteNumber("size", resource.Contract.ByteSize);
                }
                else
                {
                    writer.WriteNumber("resource", _resourceHandles[index]);
                    if (resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer)
                        writer.WriteNumber("size", _resourceSizes[index]);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    internal int SnapshotUniforms(Span<uint> offsets)
    {
        if (!IsGenerated || offsets.Length < _blocks.Length)
            throw new InvalidOperationException("WebGPU.Program.UniformsPending: program resources are not prepared.");
        for (int i = 0; i < _blocks.Length; i++)
            offsets[i] = Renderer.SnapshotEngineUniforms(_blocks[i].Bytes);
        return _blocks.Length;
    }

    private void Link(XRRenderProgram _) => Generate();

    public void SetMatrix(string name, Matrix4x4 value)
    {
        if (_uniforms.TryGetValue(name, out List<UniformTarget>? targets))
            foreach (UniformTarget target in targets) target.Block.WriteMatrix(target.Member, value);
    }

    private void SetFloat(string name, float value) => SetFloats(name, [value]);
    private void SetVector2(string name, Vector2 value) => SetFloats(name, [value.X, value.Y]);
    private void SetVector3(string name, Vector3 value) => SetFloats(name, [value.X, value.Y, value.Z]);
    public void SetVector4(string name, Vector4 value) => SetFloats(name, [value.X, value.Y, value.Z, value.W]);
    private void SetInt(string name, int value) => SetUInt(name, unchecked((uint)value));
    private void SetBool(string name, bool value) => SetUInt(name, value ? 1u : 0u);

    private void SetFloats(string name, ReadOnlySpan<float> values)
    {
        if (_uniforms.TryGetValue(name, out List<UniformTarget>? targets))
            foreach (UniformTarget target in targets) target.Block.Write(target.Member, values);
    }

    private void SetUInt(string name, uint value)
    {
        if (_uniforms.TryGetValue(name, out List<UniformTarget>? targets))
            foreach (UniformTarget target in targets) target.Block.WriteInteger(target.Member, value);
    }

    protected override void OnRetiring()
    {
        Data.LinkRequested -= Link;
        Data.UseRequested -= Link;
        Data.UniformSetMatrix4x4Requested -= SetMatrix;
        Data.UniformSetFloatRequested -= SetFloat;
        Data.UniformSetVector2Requested -= SetVector2;
        Data.UniformSetVector3Requested -= SetVector3;
        Data.UniformSetVector4Requested -= SetVector4;
        Data.UniformSetIntRequested -= SetInt;
        Data.UniformSetUIntRequested -= SetUInt;
        Data.UniformSetBoolRequested -= SetBool;
        Data.SamplerRequested -= SetSampler;
        Data.SamplerRequestedByLocation -= SetSamplerByLocation;
        Data.BindBufferRequested -= SetBuffer;
        base.OnRetiring();
    }

    public override void Destroy()
    {
        SetField(ref _preparationEpoch, checked(_preparationEpoch + 1));
        Renderer.ReleaseEngineDrawDependencies(this);
        DestroyCompute();
        ClearBindingSets();
        if (Renderer.State != BrowserRendererState.Disposed)
        {
            foreach (int layout in _layouts) if (layout != 0) Renderer.RetireEngineResourceAfterFrame(layout);
            if (_shaderHandle != 0) Renderer.RetireEngineResourceAfterFrame(_shaderHandle);
        }
        SetField(ref _layouts, []);
        SetField(ref _blocks, []);
        SetField(ref _shaderHandle, 0);
        SetField(ref _preparation, null);
        _uniforms.Clear();
        _samplers.Clear();
        SetField(ref _resourceHandles, []);
        SetField(ref _resourceSizes, []);
        SetField(ref _resourceOwners, []);
        SetField(ref _uniformArena, 0);
        Data.SetBackendLinked(false);
    }
}
