namespace XREngine.Rendering.Vulkan;

internal sealed class ComputeDispatchSnapshot
{
    public Dictionary<string, ProgramUniformValue> Uniforms { get; private set; }
    public Dictionary<uint, XRTexture> Samplers { get; private set; }
    public Dictionary<uint, string> SamplerNamesByUnit { get; private set; }
    public Dictionary<string, XRTexture> SamplersByName { get; private set; }
    public Dictionary<uint, ProgramImageBinding> Images { get; private set; }
    public Dictionary<uint, VulkanComputeBufferBinding> Buffers { get; }
    public Dictionary<string, VulkanComputeBufferBinding> BuffersByName { get; }
    private MaterialUniformBindingPayload? _materialUniformBindings;

    /// <summary>
    /// True only for a snapshot whose exact material and render scope is shared
    /// by multiple draws in the current frame.
    /// </summary>
    internal bool AllowsMaterialBindingFastPath { get; private set; }

    /// <summary>
    /// Numeric material bindings shared by this frame-local snapshot. They are
    /// immutable and intentionally stored separately from scope-owned values,
    /// so a new frame does not copy a material dictionary merely to update the
    /// camera, pass, or time values.
    /// </summary>
    internal MaterialUniformBindingPayload? MaterialUniformBindings
        => _materialUniformBindings;

    public ComputeDispatchSnapshot()
        : this(
            new Dictionary<string, ProgramUniformValue>(StringComparer.Ordinal),
            new Dictionary<uint, XRTexture>(),
            new Dictionary<uint, string>(),
            new Dictionary<string, XRTexture>(StringComparer.Ordinal),
            new Dictionary<uint, ProgramImageBinding>(),
            new Dictionary<uint, VulkanComputeBufferBinding>(),
            new Dictionary<string, VulkanComputeBufferBinding>(StringComparer.Ordinal))
    {
    }

    public ComputeDispatchSnapshot(
        Dictionary<string, ProgramUniformValue> uniforms,
        Dictionary<uint, XRTexture> samplers,
        Dictionary<uint, string> samplerNamesByUnit,
        Dictionary<string, XRTexture> samplersByName,
        Dictionary<uint, ProgramImageBinding> images,
        Dictionary<uint, VulkanComputeBufferBinding> buffers,
        Dictionary<string, VulkanComputeBufferBinding> buffersByName)
    {
        Uniforms = uniforms;
        Samplers = samplers;
        SamplerNamesByUnit = samplerNamesByUnit;
        SamplersByName = samplersByName;
        Images = images;
        Buffers = buffers;
        BuffersByName = buffersByName;
    }

    internal bool HasPublishedBindingLayoutSignatures { get; private set; }
    internal ulong UniformBindingLayoutSignature { get; private set; }
    internal ulong SamplerUnitBindingLayoutSignature { get; private set; }
    internal ulong SamplerNameBindingLayoutSignature { get; private set; }
    internal ulong ImageBindingLayoutSignature { get; private set; }
    internal ulong BufferBindingLayoutSignature { get; private set; }
    internal ulong DescriptorSetLayoutSignature { get; private set; }
    internal ulong ExactSamplerResourceSignature { get; private set; }
    internal ulong RuntimeUniformNameSignature { get; private set; }

    public ComputeDispatchSnapshot(
        Dictionary<string, ProgramUniformValue> uniforms,
        Dictionary<uint, XRTexture> samplers,
        Dictionary<uint, string> samplerNamesByUnit,
        Dictionary<string, XRTexture> samplersByName,
        Dictionary<uint, ProgramImageBinding> images,
        Dictionary<uint, XRDataBuffer> buffers)
        : this(
            uniforms,
            samplers,
            samplerNamesByUnit,
            samplersByName,
            images,
            BuildBindings(buffers),
            BuildBuffersByName(buffers))
    {
    }

    /// <summary>
    /// Replaces captured bindings while retaining dictionary storage. Desktop
    /// frame snapshots are short-lived CPU recording data, so a per-program
    /// frame pool can reuse their backing arrays after the previous frame has
    /// completed command recording.
    /// </summary>
    public void Reset(
        Dictionary<string, ProgramUniformValue> uniforms,
        Dictionary<uint, XRTexture> samplers,
        Dictionary<uint, string> samplerNamesByUnit,
        Dictionary<string, XRTexture> samplersByName,
        Dictionary<uint, ProgramImageBinding> images)
    {
        BeginNewContent();
        HasPublishedBindingLayoutSignatures = false;
        CopyUniforms(uniforms, Uniforms);
        // Ordinary uniform names and values are frame data, not Vulkan descriptor
        // layout. The linked program already owns the reflected UBO schema, so
        // hashing hundreds of material uniform names for every draw cannot make
        // command recording safer and was a dominant stable-frame CPU cost.
        UniformBindingLayoutSignature = 0;
        Copy(samplers, Samplers);
        Copy(samplerNamesByUnit, SamplerNamesByUnit);
        Copy(samplersByName, SamplersByName);
        Copy(images, Images);
        Buffers.Clear();
        BuffersByName.Clear();
        SamplerUnitBindingLayoutSignature = 0;
        SamplerNameBindingLayoutSignature = 0;
        ImageBindingLayoutSignature = 0;
        BufferBindingLayoutSignature = 0;
        DescriptorSetLayoutSignature = 0;
        ExactSamplerResourceSignature = 0;
        RuntimeUniformNameSignature = 0;
    }

    /// <summary>
    /// Exchanges the capture workspace dictionaries with this frame-owned
    /// snapshot. The writer receives the snapshot's reusable empty storage,
    /// while the immutable packet takes ownership of the bindings in O(1).
    /// </summary>
    internal void ExchangeCapturedBindings(
        ref Dictionary<string, ProgramUniformValue> uniforms,
        ref Dictionary<uint, XRTexture> samplers,
        ref Dictionary<uint, string> samplerNamesByUnit,
        ref Dictionary<string, XRTexture> samplersByName,
        ref Dictionary<uint, ProgramImageBinding> images)
    {
        BeginNewContent();
        (Uniforms, uniforms) = (uniforms, Uniforms);
        (Samplers, samplers) = (samplers, Samplers);
        (SamplerNamesByUnit, samplerNamesByUnit) = (samplerNamesByUnit, SamplerNamesByUnit);
        (SamplersByName, samplersByName) = (samplersByName, SamplersByName);
        (Images, images) = (images, Images);

        HasPublishedBindingLayoutSignatures = false;
        UniformBindingLayoutSignature = 0;
        SamplerUnitBindingLayoutSignature = 0;
        SamplerNameBindingLayoutSignature = 0;
        ImageBindingLayoutSignature = 0;
        BufferBindingLayoutSignature = 0;
        DescriptorSetLayoutSignature = 0;
        ExactSamplerResourceSignature = 0;
        RuntimeUniformNameSignature = 0;
        Buffers.Clear();
        BuffersByName.Clear();
    }

    internal void EnableMaterialBindingFastPath()
        => AllowsMaterialBindingFastPath = true;

    internal void SetMaterialUniformBindings(MaterialUniformBindingPayload? payload)
        => _materialUniformBindings = payload;

    internal bool HasRuntimeUniform(string name)
        => Uniforms.ContainsKey(name);

    private void BeginNewContent()
    {
        AllowsMaterialBindingFastPath = false;
        _materialUniformBindings = null;
    }

    /// <summary>
    /// Resolves a sampler from this captured binding set without consulting the
    /// program's mutable binding dictionaries.
    /// </summary>
    internal bool TryGetSamplerTexture(string samplerName, out XRTexture? texture)
    {
        texture = null;
        return !string.IsNullOrEmpty(samplerName) &&
            SamplersByName.TryGetValue(samplerName, out texture);
    }

    /// <summary>
    /// Publishes immutable descriptor-layout fingerprints after all captured
    /// buffer handles have been resolved. Uniform layout hashing is folded
    /// into the copy above so command-buffer signature validation does not
    /// rescan every uniform name and type later in the same frame.
    /// </summary>
    internal void PublishBindingLayoutSignatures()
    {
        SamplerUnitBindingLayoutSignature = VulkanRenderer.HashSamplerUnitBindingLayout(Samplers, SamplerNamesByUnit);
        SamplerNameBindingLayoutSignature = VulkanRenderer.HashSamplerNameBindingLayout(SamplersByName);
        ImageBindingLayoutSignature = VulkanRenderer.HashImageBindingLayout(Images);
        BufferBindingLayoutSignature = VulkanRenderer.HashBufferBindingLayout(Buffers);
        FrameOpSignatureHasher samplerResourceHash = new();
        samplerResourceHash.Add(VulkanRenderer.HashSamplerUnitBindings(
            Samplers,
            SamplerNamesByUnit,
            includeMutableFrameSourceDescriptors: true));
        samplerResourceHash.Add(VulkanRenderer.HashSamplerNameBindings(
            SamplersByName,
            includeMutableFrameSourceDescriptors: true));
        ExactSamplerResourceSignature = samplerResourceHash.ToHash();
        RuntimeUniformNameSignature = HashUniformNames(Uniforms);

        FrameOpSignatureHasher hash = new();
        hash.Add(1);
        hash.Add(SamplerUnitBindingLayoutSignature);
        hash.Add(SamplerNameBindingLayoutSignature);
        hash.Add(ImageBindingLayoutSignature);
        hash.Add(BufferBindingLayoutSignature);
        DescriptorSetLayoutSignature = hash.ToHash();
        HasPublishedBindingLayoutSignatures = true;
    }

    private static void Copy<TKey, TValue>(
        Dictionary<TKey, TValue> source,
        Dictionary<TKey, TValue> destination)
        where TKey : notnull
    {
        destination.Clear();
        destination.EnsureCapacity(source.Count);
        foreach (KeyValuePair<TKey, TValue> pair in source)
            destination[pair.Key] = pair.Value;
    }

    private static void CopyUniforms(
        Dictionary<string, ProgramUniformValue> source,
        Dictionary<string, ProgramUniformValue> destination)
    {
        destination.Clear();
        destination.EnsureCapacity(source.Count);

        foreach (KeyValuePair<string, ProgramUniformValue> pair in source)
            destination[pair.Key] = pair.Value;
    }

    private static ulong HashUniformNames(Dictionary<string, ProgramUniformValue> uniforms)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (string name in uniforms.Keys)
        {
            FrameOpSignatureHasher item = new();
            item.Add(name);
            VulkanRenderer.AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return VulkanRenderer.FinishUnorderedHash(uniforms.Count, xor, sum);
    }

    private static Dictionary<uint, VulkanComputeBufferBinding> BuildBindings(Dictionary<uint, XRDataBuffer> buffers)
    {
        Dictionary<uint, VulkanComputeBufferBinding> bindings = new(buffers.Count);
        foreach (KeyValuePair<uint, XRDataBuffer> pair in buffers)
            bindings[pair.Key] = new VulkanComputeBufferBinding(pair.Value, default, 0UL, 0);
        return bindings;
    }

    private static Dictionary<string, VulkanComputeBufferBinding> BuildBuffersByName(Dictionary<uint, XRDataBuffer> buffers)
    {
        Dictionary<string, VulkanComputeBufferBinding> buffersByName = new(StringComparer.Ordinal);
        foreach (XRDataBuffer buffer in buffers.Values)
        {
            if (!string.IsNullOrWhiteSpace(buffer.AttributeName))
                buffersByName.TryAdd(buffer.AttributeName, new VulkanComputeBufferBinding(buffer, default, 0UL, 0));
        }

        return buffersByName;
    }
}
