namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Collects the textures that a frame must have resident on the current device, each with the
/// streaming generation it requires. The required-upload manifest is built from this set, and the
/// upload ledger uses the manifest to find textures that the current Vulkan device has never
/// uploaded (for example, after a renderer restart) and to request their restoration.
/// The desktop accepted frame plan and the OpenXR eye path both fill a set, so they apply the same
/// generation and duplicate rules. All storage is allocated once; adding and clearing do not allocate.
/// </summary>
internal sealed class VulkanRequiredTextureReferenceSet(int capacity)
{
    private readonly XRTexture?[] _textures = new XRTexture?[capacity];
    private readonly long[] _generations = new long[capacity];

    /// <summary>Number of distinct textures in the set.</summary>
    internal int Count { get; private set; }

    /// <summary>The distinct textures, in the order they were first added.</summary>
    internal ReadOnlySpan<XRTexture?> Textures => _textures.AsSpan(0, Count);

    /// <summary>The required streaming generation of each texture in <see cref="Textures"/>; 0 means none is known.</summary>
    internal ReadOnlySpan<long> Generations => _generations.AsSpan(0, Count);

    /// <summary>Removes every texture and generation.</summary>
    internal void Clear()
    {
        _textures.AsSpan(0, Count).Clear();
        _generations.AsSpan(0, Count).Clear();
        Count = 0;
    }

    /// <summary>
    /// Adds the material textures of each mesh request: the resolved material, the material that
    /// supplies shadow uniforms, and the override material. Each texture requires its published
    /// generation only, because a newly pending upload must not change a frame that is already
    /// accepted.
    /// </summary>
    internal void AddMeshRequestMaterialTextures(ReadOnlySpan<VulkanMeshRenderRequest> requests)
    {
        for (int index = 0; index < requests.Length; index++)
        {
            ref readonly VulkanMeshRenderRequest request = ref requests[index];
            AddMaterialTextures(request.ResolvedMaterial.Material);
            AddMaterialTextures(request.ResolvedMaterial.ShadowUniformSourceMaterial);
            AddMaterialTextures(request.MaterialOverride);
        }
    }

    /// <summary>Adds every texture of <paramref name="material"/>; a null material adds nothing.</summary>
    internal void AddMaterialTextures(
        XRMaterial? material,
        bool includePendingUploadGeneration = false)
    {
        if (material is null)
            return;

        for (int index = 0; index < material.Textures.Count; index++)
            Add(material.Textures[index], includePendingUploadGeneration: includePendingUploadGeneration);
    }

    /// <summary>
    /// Adds <paramref name="texture"/> once. Without <paramref name="explicitGeneration"/>, an imported
    /// streamed texture requires its published generation (or the newer pending upload generation when
    /// <paramref name="includePendingUploadGeneration"/> is set). A texture that is already in the set
    /// keeps the larger of the two generations.
    /// </summary>
    internal void Add(
        XRTexture? texture,
        long explicitGeneration = 0L,
        bool includePendingUploadGeneration = false)
    {
        if (texture is null)
            return;

        long requiredGeneration = explicitGeneration;
        if (requiredGeneration <= 0L && texture is XRTexture2D texture2D &&
            ImportedTextureStreamingManager.Instance.TryGetGenerationState(
                texture2D,
                out long publishedGeneration,
                out long uploadGeneration,
                out _,
                out _))
        {
            // Before materialization, the accepted visible cohort owns the newest requested
            // generation so foreground readiness can finish it before descriptor capture. Once the
            // logical plan is sealed, only a generation that has already published may refine the
            // snapshot. Admitting a newly pending upload at that point would change the accepted
            // frame and demand descriptor publication for bindings that the sealed plan does not use.
            requiredGeneration = includePendingUploadGeneration
                ? Math.Max(publishedGeneration, uploadGeneration)
                : publishedGeneration;
        }

        for (int index = 0; index < Count; index++)
        {
            if (!ReferenceEquals(_textures[index], texture))
                continue;
            if (requiredGeneration > 0L)
                _generations[index] = Math.Max(_generations[index], requiredGeneration);
            return;
        }

        if (Count >= _textures.Length)
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Upload,
                _textures.Length,
                Count + 1);
        }

        _textures[Count] = texture;
        _generations[Count] = requiredGeneration;
        Count++;
    }
}
