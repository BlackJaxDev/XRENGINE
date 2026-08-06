using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

internal sealed class ComputeDispatchSnapshot
{
    private SpinLock _liveFrameSourceResourceSignatureLock;
    private ulong _liveFrameSourceResourceSignatureFrameId;
    private int _liveFrameSourceResourceSignaturePipelineIdentity;
    private int _liveFrameSourceResourceSignatureScopeIdentity;
    private ulong _liveFrameSourceExactSamplerResourceSignature;
    private ulong _liveFrameSourcePersistentEngineResourceSignature;
    private ulong _publishedImageResourceSignature;
    private ulong _publishedBufferResourceSignature;
    internal VulkanTextureDescriptorSignaturePlan DescriptorSignatures { get; } = new();

    public Dictionary<string, ProgramUniformValue> Uniforms { get; private set; }
    internal Dictionary<string, VulkanRuntimeUniformPublication>
        RuntimeUniformPublications { get; private set; } =
            new(StringComparer.Ordinal);
    internal HashSet<string> MutableLegacyUniformNames { get; private set; } =
        new(StringComparer.Ordinal);
    internal HashSet<string> RequiredSamplerNames { get; private set; } =
        new(StringComparer.Ordinal);
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
    internal ulong RequiredSamplerPolicySignature { get; private set; }
    internal ulong DescriptorSetLayoutSignature { get; private set; }
    internal ulong ExactSamplerResourceSignature { get; private set; }
    internal ulong RuntimeUniformNameSignature { get; private set; }
    internal ulong RuntimeUniformValueSignature { get; private set; }
    internal ulong PersistentEngineUniformSignature { get; private set; }
    internal ulong PersistentEngineResourceSignature { get; private set; }
    internal bool HasMutableFrameSourceSamplerBindings { get; private set; }
    internal ulong MutableLegacyUniformNameSignature { get; private set; }
    internal ulong MutableLegacyUniformValueSignature { get; private set; }
    internal ulong RuntimeUniformPublicationLayoutSignature { get; private set; }
    internal VulkanBindingFrequencyGenerations TypedPublicationGenerations
        { get; private set; }

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
        RequiredSamplerPolicySignature = 0;
        DescriptorSetLayoutSignature = 0;
        ExactSamplerResourceSignature = 0;
        RuntimeUniformNameSignature = 0;
        RuntimeUniformValueSignature = 0;
        PersistentEngineUniformSignature = 0;
        PersistentEngineResourceSignature = 0;
        MutableLegacyUniformNameSignature = 0;
        MutableLegacyUniformValueSignature = 0;
        RuntimeUniformPublicationLayoutSignature = 0;
        TypedPublicationGenerations = default;
        RuntimeUniformPublications.Clear();
        MutableLegacyUniformNames.Clear();
    }

    /// <summary>
    /// Exchanges the capture workspace dictionaries with this frame-owned
    /// snapshot. The writer receives the snapshot's reusable empty storage,
    /// while the immutable packet takes ownership of the bindings in O(1).
    /// </summary>
    internal void ExchangeCapturedBindings(
        ref Dictionary<string, ProgramUniformValue> uniforms,
        ref Dictionary<string, VulkanRuntimeUniformPublication>
            runtimeUniformPublications,
        ref HashSet<string> mutableLegacyUniformNames,
        ref HashSet<string> requiredSamplerNames,
        ref Dictionary<uint, XRTexture> samplers,
        ref Dictionary<uint, string> samplerNamesByUnit,
        ref Dictionary<string, XRTexture> samplersByName,
        ref Dictionary<uint, ProgramImageBinding> images)
    {
        BeginNewContent();
        (Uniforms, uniforms) = (uniforms, Uniforms);
        (RuntimeUniformPublications, runtimeUniformPublications) =
            (runtimeUniformPublications, RuntimeUniformPublications);
        (MutableLegacyUniformNames, mutableLegacyUniformNames) =
            (mutableLegacyUniformNames, MutableLegacyUniformNames);
        (RequiredSamplerNames, requiredSamplerNames) =
            (requiredSamplerNames, RequiredSamplerNames);
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
        RequiredSamplerPolicySignature = 0;
        DescriptorSetLayoutSignature = 0;
        ExactSamplerResourceSignature = 0;
        RuntimeUniformNameSignature = 0;
        RuntimeUniformValueSignature = 0;
        PersistentEngineUniformSignature = 0;
        PersistentEngineResourceSignature = 0;
        MutableLegacyUniformNameSignature = 0;
        MutableLegacyUniformValueSignature = 0;
        RuntimeUniformPublicationLayoutSignature = 0;
        TypedPublicationGenerations = default;
        Buffers.Clear();
        BuffersByName.Clear();
    }

    internal void EnableMaterialBindingFastPath()
        => AllowsMaterialBindingFastPath = true;

    internal void SetMaterialUniformBindings(MaterialUniformBindingPayload? payload)
        => _materialUniformBindings = payload;

    /// <summary>
    /// Produces a detached binding snapshot for a sealed frame plan. The capture
    /// workspace is deliberately mutable and reused by producers, so retaining
    /// any of its dictionaries would permit post-seal descriptor changes.
    /// </summary>
    internal ComputeDispatchSnapshot CreateSealedCopy()
    {
        ComputeDispatchSnapshot copy = new(
            new Dictionary<string, ProgramUniformValue>(Uniforms, StringComparer.Ordinal),
            new Dictionary<uint, XRTexture>(Samplers),
            new Dictionary<uint, string>(SamplerNamesByUnit),
            new Dictionary<string, XRTexture>(SamplersByName, StringComparer.Ordinal),
            new Dictionary<uint, ProgramImageBinding>(Images),
            new Dictionary<uint, VulkanComputeBufferBinding>(Buffers),
            new Dictionary<string, VulkanComputeBufferBinding>(BuffersByName, StringComparer.Ordinal));

        foreach ((string name, VulkanRuntimeUniformPublication publication) in RuntimeUniformPublications)
            copy.RuntimeUniformPublications.Add(name, publication);
        foreach (string name in MutableLegacyUniformNames)
            copy.MutableLegacyUniformNames.Add(name);
        foreach (string name in RequiredSamplerNames)
            copy.RequiredSamplerNames.Add(name);

        copy._materialUniformBindings = _materialUniformBindings;
        copy.AllowsMaterialBindingFastPath = AllowsMaterialBindingFastPath;
        copy.HasPublishedBindingLayoutSignatures = HasPublishedBindingLayoutSignatures;
        copy.UniformBindingLayoutSignature = UniformBindingLayoutSignature;
        copy.SamplerUnitBindingLayoutSignature = SamplerUnitBindingLayoutSignature;
        copy.SamplerNameBindingLayoutSignature = SamplerNameBindingLayoutSignature;
        copy.ImageBindingLayoutSignature = ImageBindingLayoutSignature;
        copy.BufferBindingLayoutSignature = BufferBindingLayoutSignature;
        copy.RequiredSamplerPolicySignature = RequiredSamplerPolicySignature;
        copy.DescriptorSetLayoutSignature = DescriptorSetLayoutSignature;
        copy.ExactSamplerResourceSignature = ExactSamplerResourceSignature;
        copy.RuntimeUniformNameSignature = RuntimeUniformNameSignature;
        copy.RuntimeUniformValueSignature = RuntimeUniformValueSignature;
        copy.PersistentEngineUniformSignature = PersistentEngineUniformSignature;
        copy.PersistentEngineResourceSignature = PersistentEngineResourceSignature;
        copy.HasMutableFrameSourceSamplerBindings = HasMutableFrameSourceSamplerBindings;
        copy.MutableLegacyUniformNameSignature = MutableLegacyUniformNameSignature;
        copy.MutableLegacyUniformValueSignature = MutableLegacyUniformValueSignature;
        copy.RuntimeUniformPublicationLayoutSignature = RuntimeUniformPublicationLayoutSignature;
        copy.TypedPublicationGenerations = TypedPublicationGenerations;
        copy._publishedImageResourceSignature = _publishedImageResourceSignature;
        copy._publishedBufferResourceSignature = _publishedBufferResourceSignature;
        copy.DescriptorSignatures.CopyFrom(DescriptorSignatures);
        return copy;
    }

    /// <summary>
    /// Creates an owning cross-frame artifact containing only generation-owned
    /// typed values and material sampler references. Frame/view/pass engine
    /// values are deliberately omitted because <see cref="PendingMeshDraw"/>
    /// owns their current values.
    /// </summary>
    internal ComputeDispatchSnapshot CreatePersistentProgramBindingArtifact(
        VulkanRenderer renderer,
        EUniformRequirements retainedEngineRequirements)
    {
        Dictionary<string, ProgramUniformValue> retainedUniforms =
            new(Uniforms.Count, StringComparer.Ordinal);
        foreach ((string name, ProgramUniformValue value) in Uniforms)
        {
            bool typed = RuntimeUniformPublications.ContainsKey(name);
            EUniformRequirements requirement =
                UniformRequirementsDetection.GetRequirement(name);
            if (typed || (requirement & retainedEngineRequirements) != 0)
                retainedUniforms[name] = value;
        }

        ComputeDispatchSnapshot artifact = new(
            retainedUniforms,
            new Dictionary<uint, XRTexture>(Samplers),
            new Dictionary<uint, string>(SamplerNamesByUnit),
            new Dictionary<string, XRTexture>(
                SamplersByName,
                StringComparer.Ordinal),
            new Dictionary<uint, ProgramImageBinding>(Images),
            new Dictionary<uint, VulkanComputeBufferBinding>(Buffers),
            new Dictionary<string, VulkanComputeBufferBinding>(
                BuffersByName,
                StringComparer.Ordinal));
        artifact.RuntimeUniformPublications.EnsureCapacity(
            RuntimeUniformPublications.Count);
        foreach ((string name, VulkanRuntimeUniformPublication publication) in
                 RuntimeUniformPublications)
        {
            artifact.RuntimeUniformPublications[name] = publication;
        }
        artifact.RequiredSamplerNames.EnsureCapacity(
            RequiredSamplerNames.Count);
        foreach (string name in RequiredSamplerNames)
            artifact.RequiredSamplerNames.Add(name);

        artifact.SetMaterialUniformBindings(MaterialUniformBindings);
        artifact.EnableMaterialBindingFastPath();
        artifact.PublishBindingLayoutSignatures(renderer);
        return artifact;
    }

    internal bool HasRuntimeUniform(string name)
        => Uniforms.ContainsKey(name);

    internal bool IsMutableLegacyUniform(string name)
    {
        if (MutableLegacyUniformNames.Contains(name))
            return true;

        if (name.EndsWith("_VTX", StringComparison.Ordinal))
            return MutableLegacyUniformNames.Contains(name[..^4]);

        return MutableLegacyUniformNames.Contains(
            string.Concat(name, "_VTX"));
    }

    internal bool IsSamplerReadyRequired(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
            RequiredSamplerNames.Contains(name);

    internal bool TryGetRuntimeUniformPublication(
        string name,
        out VulkanRuntimeUniformPublication publication)
        => RuntimeUniformPublications.TryGetValue(name, out publication);

    private void BeginNewContent()
    {
        AllowsMaterialBindingFastPath = false;
        _materialUniformBindings = null;
        RequiredSamplerNames.Clear();
        HasMutableFrameSourceSamplerBindings = false;
        _publishedImageResourceSignature = 0UL;
        _publishedBufferResourceSignature = 0UL;
        _liveFrameSourceResourceSignatureFrameId = 0UL;
        _liveFrameSourceResourceSignaturePipelineIdentity = 0;
        _liveFrameSourceResourceSignatureScopeIdentity = 0;
        _liveFrameSourceExactSamplerResourceSignature = 0UL;
        _liveFrameSourcePersistentEngineResourceSignature = 0UL;
        DescriptorSignatures.Clear();
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
    /// Resolves the current physical sampler identity once for each render frame
    /// and stable view family. Immutable snapshots return their publication-time
    /// signature without synchronization or descriptor dictionary walks.
    /// </summary>
    internal void ResolvePublishedResourceSignatures(
        int resourceScopeIdentity,
        out ulong exactSamplerResourceSignature,
        out ulong persistentEngineResourceSignature)
    {
        if (!HasPublishedBindingLayoutSignatures ||
            !HasMutableFrameSourceSamplerBindings)
        {
            exactSamplerResourceSignature = ExactSamplerResourceSignature;
            persistentEngineResourceSignature =
                PersistentEngineResourceSignature;
            return;
        }

        ulong frameId =
            RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        XRRenderPipelineInstance? pipeline =
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        int pipelineIdentity = pipeline is null
            ? 0
            : RuntimeHelpers.GetHashCode(pipeline);
        bool lockTaken = false;
        try
        {
            _liveFrameSourceResourceSignatureLock.Enter(ref lockTaken);
            if (frameId != 0UL &&
                _liveFrameSourceResourceSignatureFrameId == frameId &&
                _liveFrameSourceResourceSignaturePipelineIdentity ==
                    pipelineIdentity &&
                _liveFrameSourceResourceSignatureScopeIdentity ==
                    resourceScopeIdentity)
            {
                exactSamplerResourceSignature =
                    _liveFrameSourceExactSamplerResourceSignature;
                persistentEngineResourceSignature =
                    _liveFrameSourcePersistentEngineResourceSignature;
                return;
            }

            ComputeLiveFrameSourceResourceSignatures(
                out exactSamplerResourceSignature,
                out persistentEngineResourceSignature);
            if (frameId == 0UL)
                return;

            _liveFrameSourceExactSamplerResourceSignature =
                exactSamplerResourceSignature;
            _liveFrameSourcePersistentEngineResourceSignature =
                persistentEngineResourceSignature;
            _liveFrameSourceResourceSignaturePipelineIdentity =
                pipelineIdentity;
            _liveFrameSourceResourceSignatureScopeIdentity =
                resourceScopeIdentity;
            _liveFrameSourceResourceSignatureFrameId = frameId;
        }
        finally
        {
            if (lockTaken)
                _liveFrameSourceResourceSignatureLock.Exit(
                    useMemoryBarrier: true);
        }
    }

    private void ComputeLiveFrameSourceResourceSignatures(
        out ulong exactSamplerResourceSignature,
        out ulong persistentEngineResourceSignature)
    {
        FrameOpSignatureHasher samplerResourceHash = new();
        samplerResourceHash.Add(VulkanRenderer.HashSamplerUnitBindings(
            Samplers,
            SamplerNamesByUnit,
            DescriptorSignatures,
            includeMutableFrameSourceDescriptors: true));
        samplerResourceHash.Add(VulkanRenderer.HashSamplerNameBindings(
            SamplersByName,
            DescriptorSignatures,
            includeMutableFrameSourceDescriptors: true));
        exactSamplerResourceSignature = samplerResourceHash.ToHash();

        FrameOpSignatureHasher persistentEngineResources = new();
        persistentEngineResources.Add(exactSamplerResourceSignature);
        persistentEngineResources.Add(_publishedImageResourceSignature);
        persistentEngineResources.Add(_publishedBufferResourceSignature);
        persistentEngineResourceSignature =
            persistentEngineResources.ToHash();
    }

    /// <summary>
    /// Publishes immutable descriptor-layout fingerprints after all captured
    /// buffer handles have been resolved. Uniform layout hashing is folded
    /// into the copy above so command-buffer signature validation does not
    /// rescan every uniform name and type later in the same frame.
    /// </summary>
    internal void PublishBindingLayoutSignatures(VulkanRenderer renderer)
    {
        DescriptorSignatures.Capture(renderer, Samplers, SamplersByName, Images);
        XRRenderPipelineInstance? pipeline =
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        SamplerUnitBindingLayoutSignature = VulkanRenderer.HashSamplerUnitBindingLayout(Samplers, SamplerNamesByUnit);
        SamplerNameBindingLayoutSignature = VulkanRenderer.HashSamplerNameBindingLayout(SamplersByName);
        ImageBindingLayoutSignature = VulkanRenderer.HashImageBindingLayout(Images);
        BufferBindingLayoutSignature = VulkanRenderer.HashBufferBindingLayout(Buffers);
        RequiredSamplerPolicySignature =
            HashUniformNames(RequiredSamplerNames);
        FrameOpSignatureHasher samplerResourceHash = new();
        samplerResourceHash.Add(VulkanRenderer.HashSamplerUnitBindings(
            Samplers,
            SamplerNamesByUnit,
            DescriptorSignatures,
            includeMutableFrameSourceDescriptors: true));
        samplerResourceHash.Add(VulkanRenderer.HashSamplerNameBindings(
            SamplersByName,
            DescriptorSignatures,
            includeMutableFrameSourceDescriptors: true));
        ExactSamplerResourceSignature = samplerResourceHash.ToHash();
        RuntimeUniformNameSignature = HashUniformNames(Uniforms);
        RuntimeUniformValueSignature =
            VulkanRenderer.HashUniformBindings(Uniforms);
        PersistentEngineUniformSignature =
            VulkanRenderer.HashUniformBindings(
                Uniforms,
                EUniformRequirements.Lights |
                EUniformRequirements.AmbientOcclusion);
        _publishedImageResourceSignature =
            VulkanRenderer.HashImageBindings(Images, DescriptorSignatures);
        _publishedBufferResourceSignature =
            VulkanRenderer.HashBufferBindings(Buffers);
        FrameOpSignatureHasher persistentEngineResources = new();
        persistentEngineResources.Add(ExactSamplerResourceSignature);
        persistentEngineResources.Add(_publishedImageResourceSignature);
        persistentEngineResources.Add(_publishedBufferResourceSignature);
        PersistentEngineResourceSignature =
            persistentEngineResources.ToHash();
        HasMutableFrameSourceSamplerBindings =
            ContainsMutableFrameSourceSamplerBindings(pipeline);
        MutableLegacyUniformNameSignature =
            HashUniformNames(MutableLegacyUniformNames);
        MutableLegacyUniformValueSignature =
            VulkanRenderer.HashUniformBindings(
                Uniforms,
                MutableLegacyUniformNames);
        PublishRuntimeUniformPublicationSignatures();

        FrameOpSignatureHasher hash = new();
        hash.Add(1);
        hash.Add(SamplerUnitBindingLayoutSignature);
        hash.Add(SamplerNameBindingLayoutSignature);
        hash.Add(ImageBindingLayoutSignature);
        hash.Add(BufferBindingLayoutSignature);
        hash.Add(RequiredSamplerPolicySignature);
        DescriptorSetLayoutSignature = hash.ToHash();
        HasPublishedBindingLayoutSignatures = true;
    }

    private bool ContainsMutableFrameSourceSamplerBindings(
        XRRenderPipelineInstance? pipeline)
    {
        foreach ((uint unit, string name) in SamplerNamesByUnit)
            if (Samplers.ContainsKey(unit) &&
                VulkanRenderer.IsMutableFrameSourceSamplerName(
                    name,
                    pipeline))
            {
                return true;
            }

        foreach (string name in SamplersByName.Keys)
            if (VulkanRenderer.IsMutableFrameSourceSamplerName(
                    name,
                    pipeline))
            {
                return true;
            }

        return false;
    }

    private void PublishRuntimeUniformPublicationSignatures()
    {
        Span<ulong> xorByFrequency =
            stackalloc ulong[(int)EVulkanBindingFrequency.Count];
        Span<ulong> sumByFrequency =
            stackalloc ulong[(int)EVulkanBindingFrequency.Count];
        Span<int> countByFrequency =
            stackalloc int[(int)EVulkanBindingFrequency.Count];
        ulong layoutXor = 0;
        ulong layoutSum = 0;

        foreach ((string name, VulkanRuntimeUniformPublication publication)
                 in RuntimeUniformPublications)
        {
            int frequencyIndex = (int)publication.Frequency;
            if ((uint)frequencyIndex >=
                (uint)EVulkanBindingFrequency.Count)
            {
                continue;
            }

            FrameOpSignatureHasher layoutItem = new();
            layoutItem.Add(name);
            layoutItem.Add((byte)publication.Frequency);
            ulong layoutHash = layoutItem.ToHash();
            VulkanRenderer.AddUnorderedItemHash(
                ref layoutXor,
                ref layoutSum,
                layoutHash);

            FrameOpSignatureHasher generationItem = new();
            generationItem.Add(name);
            generationItem.Add(publication.Generation);
            ulong generationHash = generationItem.ToHash();
            VulkanRenderer.AddUnorderedItemHash(
                ref xorByFrequency[frequencyIndex],
                ref sumByFrequency[frequencyIndex],
                generationHash);
            countByFrequency[frequencyIndex]++;
        }

        RuntimeUniformPublicationLayoutSignature =
            VulkanRenderer.FinishUnorderedHash(
                RuntimeUniformPublications.Count,
                layoutXor,
                layoutSum);
        TypedPublicationGenerations =
            new VulkanBindingFrequencyGenerations(
                FinishFrequencyGeneration(
                    EVulkanBindingFrequency.Frame,
                    xorByFrequency,
                    sumByFrequency,
                    countByFrequency),
                FinishFrequencyGeneration(
                    EVulkanBindingFrequency.View,
                    xorByFrequency,
                    sumByFrequency,
                    countByFrequency),
                FinishFrequencyGeneration(
                    EVulkanBindingFrequency.Pass,
                    xorByFrequency,
                    sumByFrequency,
                    countByFrequency),
                FinishFrequencyGeneration(
                    EVulkanBindingFrequency.Material,
                    xorByFrequency,
                    sumByFrequency,
                    countByFrequency),
                FinishFrequencyGeneration(
                    EVulkanBindingFrequency.Object,
                    xorByFrequency,
                    sumByFrequency,
                    countByFrequency),
                FinishFrequencyGeneration(
                    EVulkanBindingFrequency.Instance,
                    xorByFrequency,
                    sumByFrequency,
                    countByFrequency),
                FinishFrequencyGeneration(
                    EVulkanBindingFrequency.RuntimeCallback,
                    xorByFrequency,
                    sumByFrequency,
                    countByFrequency));
    }

    private static ulong FinishFrequencyGeneration(
        EVulkanBindingFrequency frequency,
        ReadOnlySpan<ulong> xorByFrequency,
        ReadOnlySpan<ulong> sumByFrequency,
        ReadOnlySpan<int> countByFrequency)
    {
        int index = (int)frequency;
        if (countByFrequency[index] == 0)
            return 0UL;

        return VulkanRenderer.FinishUnorderedHash(
            countByFrequency[index],
            xorByFrequency[index],
            sumByFrequency[index]);
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

    private static ulong HashUniformNames(HashSet<string> uniformNames)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (string name in uniformNames)
        {
            FrameOpSignatureHasher item = new();
            item.Add(name);
            VulkanRenderer.AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return VulkanRenderer.FinishUnorderedHash(
            uniformNames.Count,
            xor,
            sum);
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
