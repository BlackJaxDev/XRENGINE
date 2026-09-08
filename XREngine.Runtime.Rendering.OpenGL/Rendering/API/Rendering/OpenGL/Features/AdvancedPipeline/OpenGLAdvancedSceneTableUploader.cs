using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using Silk.NET.OpenGL;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Lowers one pinned canonical scene publication into OpenGL SSBO images.
/// The owner retains a GPU publication lease until the stage runtime has proof
/// that the GL submission consuming these buffers completed.
/// </summary>
internal sealed unsafe class OpenGLAdvancedSceneTableUploader : IDisposable
{
    private const uint BindingCount = 48u;
    private const uint RequiredStageBindingCount = 75u;
    private readonly OpenGLRenderer _renderer;
    private readonly uint[] _buffers = new uint[BindingCount];
    // The visibility counter ABI references the same packed lookup image as
    // the global tables. Keep its segments with the retained publication.
    private readonly uint[] _visibilityCounterSegments = new uint[22];
    private AdvancedGpuHandleLookup[] _lookupScratch = [];
    private AdvancedTextureRecord[] _textureScratch = [];
    private AdvancedMaterialTextureBinding[] _materialBindingScratch = [];
    private AdvancedEncodedTextureReference[] _encodedTextureScratch = [];
    private ulong[] _residentPairHandles = [];
    private byte[] _samplerValidationScratch = [];
    private int _residentPairHandleCount;
    private AdvancedGpuScenePublicationLease _lease;
    private ulong _uploadedSequence;
    private ulong _uploadedDatabaseEpoch;
    private uint _maxBindings;
    private bool _initialized;
    private OpenGLAdvancedSceneArena? _sceneArena;
    internal uint LightCount { get; private set; }
    internal uint StaticVertexBuffer => _buffers[(int)AdvancedReconstructionShaderBindings.StaticVertices];
    internal uint IndexBuffer => _buffers[(int)AdvancedReconstructionShaderBindings.Indices];

    internal OpenGLAdvancedSceneTableUploader(OpenGLRenderer renderer)
        => _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    internal bool IsCurrent(in AdvancedGpuScenePublication publication)
        => _lease.IsValid && _uploadedDatabaseEpoch == publication.DatabaseEpoch &&
           _uploadedSequence == publication.Sequence;

    internal bool TryUpload(
        AdvancedSharedGpuSceneDatabase database,
        in AdvancedGpuScenePublication publication,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!publication.IsValid || publication.DatabaseEpoch != database.DatabaseEpoch)
        {
            reason = "The GL advanced-scene publication does not belong to the supplied canonical database.";
            return false;
        }
        if (IsCurrent(in publication))
        {
            BindAll();
            reason = "Ready";
            return true;
        }
        if (_lease.IsValid)
        {
            reason = "The preceding OpenGL Advanced scene publication is still GPU-owned; its SSBO image cannot be overwritten yet.";
            return false;
        }
        if (!EnsureInitialized(out reason))
            return false;

        AdvancedGpuScenePublicationReference reference = new(publication);
        if (!database.TryAcquirePublicationLease(
                in reference,
                EAdvancedGpuScenePublicationPinKind.Gpu,
                out AdvancedGpuScenePublicationLease nextLease))
        {
            reason = "The canonical Advanced publication could not acquire a GL GPU lease.";
            return false;
        }

        bool committed = false;
        try
        {
            if (!database.TryGetPublicationSnapshot(in reference,
                    out AdvancedGpuScenePublicationSnapshot snapshot) ||
                !HasExactSequence(snapshot, publication.Sequence))
            {
                reason = "The canonical Advanced publication snapshot is unavailable or sequence-mismatched.";
                return false;
            }

            if (!TryUploadSnapshot(snapshot, out reason))
                return false;

            _lease = nextLease;
            _uploadedDatabaseEpoch = publication.DatabaseEpoch;
            _uploadedSequence = publication.Sequence;
            committed = true;
            reason = "Ready";
            return true;
        }
        finally
        {
            if (!committed)
            {
                nextLease.Dispose();
                ReleaseResidentPairHandles();
            }
        }
    }

    internal void BindAll()
    {
        for (uint binding = 0u; binding < BindingCount; ++binding)
            _renderer.RawGL.BindBufferBase(
                GLEnum.ShaderStorageBuffer, binding, _buffers[binding]);
        _sceneArena?.TryBind(out _);
    }

    /// <summary>Writes the eleven sealed logical-handle lookup segments into
    /// every 38-word visibility counter record.</summary>
    internal bool TryInitializeVisibilityCounters(
        Span<uint> counters,
        uint viewCount,
        out string reason)
    {
        const int counterWordsPerView = 38;
        const int segmentWordOffset = 16;
        if (!_lease.IsValid || viewCount == 0u ||
            counters.Length < checked((int)viewCount * counterWordsPerView))
        {
            reason = "The OpenGL Advanced scene publication has no sealed counter lookup image.";
            return false;
        }

        for (uint view = 0u; view < viewCount; ++view)
            _visibilityCounterSegments.CopyTo(counters.Slice(
                checked((int)view * counterWordsPerView + segmentWordOffset),
                _visibilityCounterSegments.Length));
        reason = "Ready";
        return true;
    }

    /// <summary>Aliases the canonical immutable geometry atlases into the
    /// OpenGL-only visibility binding range without duplicating their data.</summary>
    internal bool TryBindVisibilityGeometry(out uint staticVertices, out uint indices)
    {
        staticVertices = _buffers[(int)AdvancedReconstructionShaderBindings.StaticVertices];
        indices = _buffers[(int)AdvancedReconstructionShaderBindings.Indices];
        if (!_initialized || staticVertices == 0u || indices == 0u)
            return false;
        _renderer.RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, 61u, staticVertices);
        _renderer.RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, 62u,
            _buffers[(int)AdvancedReconstructionShaderBindings.PreSkinnedCurrentVertices]);
        _renderer.RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, 63u,
            _buffers[(int)AdvancedReconstructionShaderBindings.PreSkinnedPreviousVertices]);
        return true;
    }

    /// <summary>
    /// Updates the global view table from the exact frozen authoring family.
    /// Collection can precede temporal-history resolution or an XR eye locate;
    /// the package still owns the canonical scene and submission templates.
    /// </summary>
    internal bool TryUploadViews(
        Commands.BackendReadyFramePackage package,
        in RenderFrameViewSet views,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!_lease.IsValid || package.State != Commands.EBackendReadyFramePackageState.Published)
        {
            reason = "The OpenGL Advanced view table has no retained canonical scene publication.";
            return false;
        }
        if (views.ViewCount is < 1 or > 2)
        {
            reason = "The OpenGL Advanced view table does not match the sealed native view family.";
            return false;
        }

        Span<AdvancedViewRecord> records = stackalloc AdvancedViewRecord[RenderFrameViewSet.MaxViewCount];
        for (int index = 0; index < views.ViewCount; ++index)
        {
            RenderFrameViewDescriptor view = views.GetView(index);
            Commands.BackendReadyCanonicalViewRecord canonical = Commands.BackendReadyFramePackage.CreateCanonicalViewRecord(
                in view, package.CanonicalScenePublication.FrameGeneration);
            records[index] = AdvancedViewRecordFactory.Create(in canonical);
        }
        Upload(AdvancedGlobalResourceBindings.Views, records[..views.ViewCount]);
        BindAll();
        reason = "The canonical OpenGL scene arena is unavailable.";
        if (_sceneArena is null || !_sceneArena.TryFinalizeAndBind(out reason))
            return false;
        reason = "Ready";
        return true;
    }

    /// <summary>
    /// Transfers the retained publication to an actual GL submission. Call once
    /// after the final stage command that reads these tables; replacement polls
    /// the fence and never waits on the render thread.
    /// </summary>
    internal bool TryMarkCurrentSubmission(out string reason)
    {
        if (!_lease.IsValid)
        {
            reason = "No canonical GL Advanced publication is retained.";
            return false;
        }
        reason = "Ready";
        return true;
    }

    private bool EnsureInitialized(out string reason)
    {
        if (_initialized)
        {
            reason = "Ready";
            return true;
        }
        if (!RuntimeEngine.IsRenderThread)
        {
            reason = "OpenGL Advanced scene tables must initialize on the render thread.";
            return false;
        }

        int maxBindings = _renderer.RawGL.GetInteger(GLEnum.MaxShaderStorageBufferBindings);
        if (maxBindings < RequiredStageBindingCount)
        {
            reason = $"OpenGL exposes {maxBindings} SSBO bindings, but Advanced global and visibility-local tables require {RequiredStageBindingCount}.";
            return false;
        }

        for (int index = 0; index < _buffers.Length; ++index)
        {
            uint buffer = _renderer.RawGL.CreateBuffer();
            if (buffer == 0u)
            {
                reason = "OpenGL could not allocate the complete Advanced scene-table binding set.";
                DisposeNativeBuffers();
                return false;
            }
            _buffers[index] = buffer;
            uint zero = 0u;
            _renderer.RawGL.NamedBufferData(buffer, sizeof(uint), &zero, GLEnum.DynamicDraw);
        }
        _maxBindings = checked((uint)maxBindings);
        _initialized = true;
        reason = "Ready";
        return true;
    }

    private bool TryUploadSnapshot(AdvancedGpuScenePublicationSnapshot snapshot, out string reason)
    {
        _sceneArena ??= new OpenGLAdvancedSceneArena(_renderer,
            checked((int)Math.Min(int.MaxValue, _renderer.RawGL.GetInteger64(GLEnum.MaxShaderStorageBlockSize))));
        _sceneArena.Begin();
        LightCount = checked((uint)snapshot.GlobalResources.Lights.PhysicalRecords.Length);
        Upload(AdvancedGlobalResourceBindings.Draws, snapshot.Draws.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.Instances, snapshot.Instances.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.Meshes, snapshot.Geometry.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.Materials, snapshot.Materials.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.Lights, snapshot.GlobalResources.Lights.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.Shadows, snapshot.GlobalResources.Shadows.PhysicalRecords);
        if (!TryLowerTextureReferences(snapshot, out reason))
            return false;
        Upload(AdvancedGlobalResourceBindings.Textures, _textureScratch.AsSpan(0, snapshot.Textures.PhysicalRecords.Length));
        Upload(AdvancedGlobalResourceBindings.Samplers, snapshot.Samplers.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.Deformations, snapshot.Deformations.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.MaterialConstants, snapshot.MaterialPayloads.ConstantWords);
        Upload(AdvancedGlobalResourceBindings.MaterialTextureBindings, _materialBindingScratch.AsSpan(0, snapshot.MaterialPayloads.TextureBindings.Length));
        Upload(AdvancedGlobalResourceBindings.EncodedTextures, _encodedTextureScratch.AsSpan(0, checked(1 + snapshot.Textures.PhysicalRecords.Length + snapshot.MaterialPayloads.TextureBindings.Length)));
        Span<AdvancedEncodedSamplerReference> encodedSamplerFallback = stackalloc AdvancedEncodedSamplerReference[1]
        {
            new((uint)EAdvancedResourceFallback.Zero, 0u, 0u, EAdvancedResourceReferenceFlags.Fallback),
        };
        Upload(AdvancedGlobalResourceBindings.EncodedSamplers, encodedSamplerFallback);
        Upload(AdvancedGlobalResourceBindings.Probes, snapshot.GlobalResources.Probes.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.Environments, snapshot.GlobalResources.Environments.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.Decals, snapshot.GlobalResources.Decals.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.GiResources, snapshot.GlobalResources.GiResources.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.Transforms, snapshot.Transforms.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.RenderStates, snapshot.RenderStates.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.ShadingKernels, snapshot.Kernels.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.MaterialLayouts, snapshot.Layouts.PhysicalRecords);
        Upload(AdvancedGlobalResourceBindings.EditorIdentities, snapshot.EditorIdentities.PhysicalRecords);
        Upload(AdvancedReconstructionShaderBindings.StaticVertices, snapshot.GeometryPayloads.StaticVertices.Data);
        Upload(AdvancedReconstructionShaderBindings.PreSkinnedCurrentVertices, snapshot.GeometryPayloads.PreSkinnedCurrent.Data);
        Upload(AdvancedReconstructionShaderBindings.PreSkinnedPreviousVertices, snapshot.GeometryPayloads.PreSkinnedPrevious.Data);
        Upload(AdvancedReconstructionShaderBindings.Indices, snapshot.GeometryPayloads.Indices.Data);
        BuildLookupImage(snapshot);
        Upload(AdvancedGlobalResourceBindings.HandleLookups, _lookupScratch);
        BindAll();
        reason = "Ready";
        return true;
    }

    private bool TryLowerTextureReferences(AdvancedGpuScenePublicationSnapshot snapshot, out string reason)
    {
        reason = string.Empty;
        if (!TryValidatePublicationSources(snapshot, out reason))
            return false;
        ReadOnlySpan<AdvancedTextureRecord> textures = snapshot.Textures.PhysicalRecords;
        ReadOnlySpan<AdvancedMaterialTextureBinding> bindings = snapshot.MaterialPayloads.TextureBindings;
        EnsureTextureScratch(textures.Length, bindings.Length);
        textures.CopyTo(_textureScratch);
        bindings.CopyTo(_materialBindingScratch);
        int pairCount = checked(1 + textures.Length + bindings.Length);
        Span<AdvancedEncodedTextureReference> pairs = _encodedTextureScratch.AsSpan(0, pairCount);
        pairs[0] = new(0u, 0u, (uint)EAdvancedResourceFallback.Zero, EAdvancedResourceReferenceFlags.Fallback);

        for (int dense = 0; dense < textures.Length; dense++)
        {
            ulong nativeHandle = 0u;
            pairs[dense + 1] = pairs[0];
            if (!TryResolveTextureRow(snapshot.Textures, dense, out AdvancedGpuHandle handle, out reason))
                return false;
            if (!handle.IsValid)
                continue;
            if (!snapshot.ResourcePayloads.TryGetTextureSource(handle, out XRTexture source))
            {
                reason = $"The sealed OpenGL texture row {dense} has no retained source.";
                return false;
            }
            if (!_textureScratch[dense].DefaultSampler.IsValid ||
                !snapshot.Samplers.TryGet(_textureScratch[dense].DefaultSampler, out AdvancedSamplerRecord sampler))
            {
                reason = $"The sealed OpenGL texture row {dense} ('{source.Name}') has no live default sampler.";
                return false;
            }
            if (!_renderer.TryGetResidentBindlessTextureSamplerHandle(source, in sampler, out nativeHandle, out reason))
                return false;
            _textureScratch[dense].EncodedReferenceIndex = checked((uint)dense + 1u);
            RecordResidentPairHandle(nativeHandle);
            pairs[dense + 1] = EncodePair(nativeHandle, _textureScratch[dense].Dimension);
        }

        int bindingBase = 1 + textures.Length;
        for (int index = 0; index < bindings.Length; index++)
        {
            ulong nativeHandle = 0u;
            AdvancedTextureRecord texture = default;
            pairs[bindingBase + index] = pairs[0];
            AdvancedMaterialTextureBinding binding = _materialBindingScratch[index];
            if (!binding.Texture.Handle.IsValid) continue;
            if (!snapshot.ResourcePayloads.TryGetTextureSource(binding.Texture.Handle, out XRTexture source) ||
                !snapshot.Textures.TryGet(binding.Texture.Handle, out texture))
            {
                reason = $"The sealed OpenGL material binding {index} has no live texture source.";
                return false;
            }
            if (!binding.Sampler.Handle.IsValid ||
                !snapshot.Samplers.TryGet(binding.Sampler.Handle, out AdvancedSamplerRecord sampler))
            {
                reason = $"The sealed OpenGL material binding {index} ('{source.Name}') has no live sampler.";
                return false;
            }
            if (!_renderer.TryGetResidentBindlessTextureSamplerHandle(source, in sampler, out nativeHandle, out reason))
                return false;
            _materialBindingScratch[index] = binding with
            {
                Texture = binding.Texture with { Reserved = checked((uint)(bindingBase + index)) },
            };
            RecordResidentPairHandle(nativeHandle);
            pairs[bindingBase + index] = EncodePair(nativeHandle, texture.Dimension);
        }
        reason = "Ready";
        return true;
    }

    /// <summary>
    /// Resolves a live texture row while leaving holes and retained tombstones
    /// unbound. Tombstones keep their physical storage until older publications
    /// retire, but their exact invalid lookup prevents this publication using it.
    /// </summary>
    private static bool TryResolveTextureRow(
        AdvancedGpuRecordTablePublicationSnapshot<AdvancedTextureRecord> textures,
        int denseIndex,
        out AdvancedGpuHandle handle,
        out string reason)
    {
        handle = AdvancedGpuHandle.Invalid;
        reason = string.Empty;
        if (textures.PhysicalOccupancy[denseIndex] == 0)
            return true;

        AdvancedGpuHandle physicalHandle = textures.PhysicalHandles[denseIndex];
        if (physicalHandle.IsValid && physicalHandle.Index < (uint)textures.HandleLookups.Length)
        {
            if (textures.HandleLookups[checked((int)physicalHandle.Index)] == AdvancedGpuHandleLookup.Invalid)
                return true;
            if (textures.TryGetDenseIndex(physicalHandle, out uint liveDenseIndex) && liveDenseIndex == denseIndex)
            {
                handle = physicalHandle;
                return true;
            }
        }

        reason = $"The sealed OpenGL texture row {denseIndex} has inconsistent logical/physical handle metadata.";
        return false;
    }

    /// <summary>
    /// Revalidates every retained logical texture and sampler against the
    /// strong source captured at publication seal. Bindless realization is
    /// deliberately deferred until this witness succeeds so mutable texture
    /// state cannot be smuggled into a frozen GL table image.
    /// </summary>
    private bool TryValidatePublicationSources(
        AdvancedGpuScenePublicationSnapshot snapshot,
        out string reason)
    {
        AdvancedGpuResourcePublicationSnapshot resources = snapshot.ResourcePayloads;
        int samplerHighWater = snapshot.Samplers.PhysicalRecords.Length;
        if (_samplerValidationScratch.Length < samplerHighWater)
            Array.Resize(ref _samplerValidationScratch, samplerHighWater);
        _samplerValidationScratch.AsSpan(0, samplerHighWater).Clear();

        ReadOnlySpan<AdvancedTextureRecord> textureRecords = snapshot.Textures.PhysicalRecords;
        for (int denseIndex = 0; denseIndex < textureRecords.Length; ++denseIndex)
        {
            if (!TryResolveTextureRow(snapshot.Textures, denseIndex, out AdvancedGpuHandle handle, out reason))
                return false;
            if (!handle.IsValid)
                continue;

            AdvancedTextureRecord canonical = textureRecords[denseIndex];
            if (!handle.IsValid || canonical.StableTextureId != handle.Index ||
                canonical.Generation != handle.Generation ||
                canonical.EncodedReferenceIndex != 0u)
            {
                reason = $"Canonical GL texture row {denseIndex} has inconsistent handle metadata for {handle.Index}:{handle.Generation}.";
                return false;
            }
            if (!resources.TryGetTextureSource(
                    handle,
                    out XRTexture source,
                    out ulong capturedSourceContentGeneration))
            {
                reason = $"Canonical GL texture {handle.Index}:{handle.Generation} has no retained strong source reference.";
                return false;
            }
            if (!AdvancedGpuResourceSourceEncoder.TryEncode(
                    source,
                    EAdvancedResourceFallback.Zero,
                    out AdvancedGpuResourceBindingSource current,
                    out _,
                    out string encodeReason))
            {
                reason = $"Canonical GL texture {handle.Index}:{handle.Generation} source revalidation failed: {encodeReason}";
                return false;
            }
            if (current.SourceContentGeneration != capturedSourceContentGeneration ||
                !canonical.DefaultSampler.IsValid ||
                !snapshot.Samplers.TryGet(canonical.DefaultSampler, out AdvancedSamplerRecord canonicalDefaultSampler) ||
                !snapshot.Samplers.TryGetDenseIndex(canonical.DefaultSampler, out uint defaultSamplerDense) ||
                !TextureMetadataStateEquals(current.TextureRecord, canonical) ||
                !SamplerStateEquals(current.SamplerRecord, canonicalDefaultSampler))
            {
                reason = $"Canonical GL texture {handle.Index}:{handle.Generation} source '{source.Name ?? source.GetType().Name}' changed after publication.";
                return false;
            }
            _samplerValidationScratch[checked((int)defaultSamplerDense)] = 1;
        }

        ReadOnlySpan<AdvancedMaterialTextureBinding> bindings =
            snapshot.MaterialPayloads.TextureBindings;
        for (int bindingIndex = 0; bindingIndex < bindings.Length; ++bindingIndex)
        {
            AdvancedMaterialTextureBinding binding = bindings[bindingIndex];
            AdvancedGpuHandle textureHandle = binding.Texture.Handle;
            if (!textureHandle.IsValid)
                continue;
            if (!binding.Sampler.Handle.IsValid ||
                !snapshot.Textures.TryGet(textureHandle, out AdvancedTextureRecord canonicalTexture) ||
                !snapshot.Samplers.TryGet(binding.Sampler.Handle, out AdvancedSamplerRecord canonicalSampler) ||
                !resources.TryGetTextureSource(textureHandle, out XRTexture source))
            {
                reason = $"GL material texture binding {bindingIndex} does not resolve its retained texture/sampler publication.";
                return false;
            }
            if (!AdvancedGpuResourceSourceEncoder.TryEncode(
                    source,
                    binding.Texture.Fallback,
                    out AdvancedGpuResourceBindingSource current,
                    out _,
                    out string encodeReason))
            {
                reason = $"GL material texture binding {bindingIndex} source revalidation failed: {encodeReason}";
                return false;
            }
            if (!canonicalTexture.DefaultSampler.IsValid ||
                !snapshot.Samplers.TryGet(canonicalTexture.DefaultSampler, out _) ||
                !TextureMetadataStateEquals(current.TextureRecord, canonicalTexture) ||
                !SamplerStateEquals(current.SamplerRecord, canonicalSampler))
            {
                reason = $"GL material texture binding {bindingIndex} source '{source.Name ?? source.GetType().Name}' changed after publication.";
                return false;
            }
            if (!snapshot.Samplers.TryGetDenseIndex(binding.Sampler.Handle, out uint samplerDense))
            {
                reason = $"GL material texture binding {bindingIndex} sampler handle is absent from the retained dense image.";
                return false;
            }
            _samplerValidationScratch[checked((int)samplerDense)] = 1;
        }

        for (int denseIndex = 0; denseIndex < samplerHighWater; ++denseIndex)
            if (snapshot.Samplers.PhysicalOccupancy[denseIndex] != 0 &&
                snapshot.Samplers.TryGetDenseIndex(
                    snapshot.Samplers.PhysicalHandles[denseIndex], out uint liveDenseIndex) &&
                liveDenseIndex == denseIndex && _samplerValidationScratch[denseIndex] == 0)
            {
                AdvancedGpuHandle handle = snapshot.Samplers.PhysicalHandles[denseIndex];
                reason = $"Canonical GL sampler {handle.Index}:{handle.Generation} has no retained texture or material binding that can revalidate its source state.";
                return false;
            }

        reason = "Ready";
        return true;
    }

    private static bool TextureMetadataStateEquals(
        in AdvancedTextureRecord left,
        in AdvancedTextureRecord right)
    {
        AdvancedTextureRecord leftWithoutSampler = left;
        AdvancedTextureRecord rightWithoutSampler = right;
        leftWithoutSampler.DefaultSampler = AdvancedGpuHandle.Invalid;
        rightWithoutSampler.DefaultSampler = AdvancedGpuHandle.Invalid;
        return leftWithoutSampler.Dimension == rightWithoutSampler.Dimension &&
            leftWithoutSampler.Flags == rightWithoutSampler.Flags &&
            leftWithoutSampler.Width == rightWithoutSampler.Width &&
            leftWithoutSampler.Height == rightWithoutSampler.Height &&
            leftWithoutSampler.DepthOrLayers == rightWithoutSampler.DepthOrLayers &&
            leftWithoutSampler.MipCount == rightWithoutSampler.MipCount &&
            leftWithoutSampler.FormatClass == rightWithoutSampler.FormatClass &&
            VectorBitsEqual(leftWithoutSampler.UvScaleBias, rightWithoutSampler.UvScaleBias);
    }

    private static bool SamplerStateEquals(
        in AdvancedSamplerRecord left,
        in AdvancedSamplerRecord right)
        => left.Filter == right.Filter && left.Flags == right.Flags &&
           left.AddressU == right.AddressU && left.AddressV == right.AddressV &&
           left.AddressW == right.AddressW && left.CompareOperation == right.CompareOperation &&
           VectorBitsEqual(left.LodBiasMinMaxAnisotropy, right.LodBiasMinMaxAnisotropy) &&
           VectorBitsEqual(left.BorderColor, right.BorderColor);

    private static bool VectorBitsEqual(Vector4 left, Vector4 right)
        => BitConverter.SingleToInt32Bits(left.X) == BitConverter.SingleToInt32Bits(right.X) &&
           BitConverter.SingleToInt32Bits(left.Y) == BitConverter.SingleToInt32Bits(right.Y) &&
           BitConverter.SingleToInt32Bits(left.Z) == BitConverter.SingleToInt32Bits(right.Z) &&
           BitConverter.SingleToInt32Bits(left.W) == BitConverter.SingleToInt32Bits(right.W);

    private void EnsureTextureScratch(int textureCount, int bindingCount)
    {
        if (_textureScratch.Length < textureCount) Array.Resize(ref _textureScratch, textureCount);
        if (_materialBindingScratch.Length < bindingCount) Array.Resize(ref _materialBindingScratch, bindingCount);
        int required = checked(1 + textureCount + bindingCount);
        if (_encodedTextureScratch.Length < required) Array.Resize(ref _encodedTextureScratch, required);
    }

    private static AdvancedEncodedTextureReference EncodePair(ulong handle, EAdvancedTextureDimension dimension)
        => new((uint)handle, (uint)(handle >> 32), (uint)dimension, EAdvancedResourceReferenceFlags.Resident);

    private void RecordResidentPairHandle(ulong handle)
    {
        if (_residentPairHandleCount == _residentPairHandles.Length)
            Array.Resize(ref _residentPairHandles, Math.Max(8, _residentPairHandles.Length * 2));
        _residentPairHandles[_residentPairHandleCount++] = handle;
    }

    private void ReleaseResidentPairHandles()
    {
        for (int index = 0; index < _residentPairHandleCount; index++)
            _renderer.ReleaseAdvancedBindlessTextureSamplerHandle(_residentPairHandles[index]);
        _residentPairHandles.AsSpan(0, _residentPairHandleCount).Clear();
        _residentPairHandleCount = 0;
    }

    private void BuildLookupImage(AdvancedGpuScenePublicationSnapshot snapshot)
    {
        int required = checked(
            snapshot.Draws.HandleLookups.Length + snapshot.Instances.HandleLookups.Length +
            snapshot.Transforms.HandleLookups.Length + snapshot.Deformations.HandleLookups.Length +
            snapshot.RenderStates.HandleLookups.Length + snapshot.EditorIdentities.HandleLookups.Length +
            snapshot.Geometry.HandleLookups.Length + snapshot.Materials.HandleLookups.Length +
            snapshot.Kernels.HandleLookups.Length + snapshot.Layouts.HandleLookups.Length +
            snapshot.Textures.HandleLookups.Length + snapshot.Samplers.HandleLookups.Length +
            snapshot.GlobalResources.Shadows.HandleLookups.Length);
        if (_lookupScratch.Length < required)
            Array.Resize(ref _lookupScratch, required);
        int offset = 0;
        _visibilityCounterSegments.AsSpan().Clear();
        Copy(snapshot.Draws.HandleLookups, 0); Copy(snapshot.Instances.HandleLookups, 2);
        Copy(snapshot.Transforms.HandleLookups, 4); Copy(snapshot.Deformations.HandleLookups);
        Copy(snapshot.RenderStates.HandleLookups, 6); Copy(snapshot.EditorIdentities.HandleLookups, 8);
        Copy(snapshot.Geometry.HandleLookups, 10); Copy(snapshot.Materials.HandleLookups, 12);
        Copy(snapshot.Kernels.HandleLookups, 14); Copy(snapshot.Layouts.HandleLookups);
        Copy(snapshot.Textures.HandleLookups, 16); Copy(snapshot.Samplers.HandleLookups, 18);
        Copy(snapshot.GlobalResources.Shadows.HandleLookups, 20);
        return;

        void Copy(ReadOnlySpan<AdvancedGpuHandleLookup> source, int segmentWordOffset = -1)
        {
            if (segmentWordOffset >= 0)
            {
                _visibilityCounterSegments[segmentWordOffset] = checked((uint)offset);
                _visibilityCounterSegments[segmentWordOffset + 1] = checked((uint)source.Length);
            }
            source.CopyTo(_lookupScratch.AsSpan(offset));
            offset += source.Length;
        }
    }

    private void Upload<T>(uint binding, ReadOnlySpan<T> source) where T : unmanaged
    {
        if (binding < 30u && binding != AdvancedGlobalResourceBindings.Diagnostics)
        {
            if (_sceneArena is null)
                throw new InvalidOperationException("The OpenGL canonical scene arena is unavailable.");
            if (!_sceneArena.TryWrite(binding, source, out string arenaReason))
                throw new InvalidOperationException(arenaReason);
            return;
        }
        if (binding >= _maxBindings)
            return;
        if (source.IsEmpty)
        {
            uint zero = 0u;
            _renderer.RawGL.NamedBufferData(_buffers[binding], sizeof(uint), &zero, GLEnum.DynamicDraw);
            return;
        }
        nuint bytes = checked((nuint)(source.Length * Unsafe.SizeOf<T>()));
        fixed (T* pointer = source)
            _renderer.RawGL.NamedBufferData(_buffers[binding], bytes, pointer, GLEnum.DynamicDraw);
    }

    private void Upload(uint binding, ReadOnlySpan<byte> source)
    {
        if (binding >= _maxBindings)
            return;
        if (source.IsEmpty)
        {
            uint zero = 0u;
            _renderer.RawGL.NamedBufferData(_buffers[binding], sizeof(uint), &zero, GLEnum.DynamicDraw);
            return;
        }
        fixed (byte* pointer = source)
            _renderer.RawGL.NamedBufferData(_buffers[binding], (nuint)source.Length, pointer, GLEnum.DynamicDraw);
    }

    private static bool HasExactSequence(AdvancedGpuScenePublicationSnapshot snapshot, ulong sequence)
        => snapshot.Draws.Sequence == sequence && snapshot.Instances.Sequence == sequence &&
           snapshot.Transforms.Sequence == sequence && snapshot.Deformations.Sequence == sequence &&
           snapshot.RenderStates.Sequence == sequence && snapshot.EditorIdentities.Sequence == sequence &&
           snapshot.Geometry.Sequence == sequence && snapshot.Materials.Sequence == sequence &&
           snapshot.Kernels.Sequence == sequence && snapshot.Layouts.Sequence == sequence &&
           snapshot.ResourcePayloads.Sequence == sequence && snapshot.GlobalResources.Sequence == sequence;

    public void Dispose()
    {
        ReleaseResidentPairHandles();
        _lease.Dispose();
        _lease = default;
        _uploadedSequence = 0u;
        _uploadedDatabaseEpoch = 0u;
        DisposeNativeBuffers();
    }

    private void DisposeNativeBuffers()
    {
        _sceneArena?.Dispose();
        _sceneArena = null;
        if (RuntimeEngine.IsRenderThread)
            for (int index = 0; index < _buffers.Length; ++index)
                if (_buffers[index] != 0u)
                    _renderer.RawGL.DeleteBuffer(_buffers[index]);
        Array.Clear(_buffers);
        _initialized = false;
    }

    internal void ReleaseCompletedSlotPublication()
    {
        if (!_lease.IsValid)
            return;
        _lease.Dispose();
        _lease = default;
        _uploadedDatabaseEpoch = 0u;
        _uploadedSequence = 0u;
        ReleaseResidentPairHandles();
    }
}
