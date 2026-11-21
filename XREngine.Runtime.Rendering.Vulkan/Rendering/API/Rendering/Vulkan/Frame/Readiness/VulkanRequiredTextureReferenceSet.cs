using System.Numerics;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Collects the textures that a frame must have resident on the current device, each with the
/// streaming generation it requires. The required-upload manifest is built from this set, and the
/// upload ledger uses the manifest to find textures that the current Vulkan device has never
/// uploaded (for example, after a renderer restart) and to request their restoration.
/// The desktop accepted frame plan and the OpenXR eye path both fill a set, so they apply the same
/// generation and duplicate rules. All storage is allocated once; adding and clearing do not allocate.
/// A hash index finds a texture that is already in the set in constant time, so a frame with
/// thousands of textures (for example, a crowd of imported avatars) does not compare each new
/// texture with every earlier one.
/// </summary>
internal sealed class VulkanRequiredTextureReferenceSet
{
    private readonly XRTexture?[] _textures;
    private readonly long[] _generations;

    /// <summary>
    /// Open-addressing hash index over <see cref="_textures"/>. Each slot holds the texture index
    /// plus 1, or 0 when the slot is empty. The length is a power of two and at least twice the
    /// capacity, so a probe always finds an empty slot.
    /// </summary>
    private readonly int[] _index;

    /// <summary>The index slots that <see cref="Add"/> filled, so <see cref="Clear"/> empties only those.</summary>
    private readonly int[] _usedIndexSlots;

    /// <summary>Creates a set that holds at most <paramref name="capacity"/> distinct textures.</summary>
    internal VulkanRequiredTextureReferenceSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _textures = new XRTexture?[capacity];
        _generations = new long[capacity];
        _index = new int[(int)BitOperations.RoundUpToPowerOf2((uint)checked(capacity * 2))];
        _usedIndexSlots = new int[capacity];
    }

    /// <summary>Number of distinct textures in the set.</summary>
    internal int Count { get; private set; }

    /// <summary>The distinct textures, in the order they were first added.</summary>
    internal ReadOnlySpan<XRTexture?> Textures => _textures.AsSpan(0, Count);

    /// <summary>The required streaming generation of each texture in <see cref="Textures"/>; 0 means none is known.</summary>
    internal ReadOnlySpan<long> Generations => _generations.AsSpan(0, Count);

    /// <summary>Removes every texture and generation.</summary>
    internal void Clear()
    {
        for (int index = 0; index < Count; index++)
            _index[_usedIndexSlots[index]] = 0;
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

        int mask = _index.Length - 1;
        int slot = RuntimeHelpers.GetHashCode(texture) & mask;
        while (true)
        {
            int stored = _index[slot];
            if (stored == 0)
                break;
            int existing = stored - 1;
            if (ReferenceEquals(_textures[existing], texture))
            {
                if (requiredGeneration > 0L)
                    _generations[existing] = Math.Max(_generations[existing], requiredGeneration);
                return;
            }
            slot = (slot + 1) & mask;
        }

        if (Count >= _textures.Length)
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Upload,
                _textures.Length,
                Count + 1,
                "The frame references more distinct textures than the required-texture set holds.");
        }

        _textures[Count] = texture;
        _generations[Count] = requiredGeneration;
        _index[slot] = Count + 1;
        _usedIndexSlots[Count] = slot;
        Count++;
    }
}
