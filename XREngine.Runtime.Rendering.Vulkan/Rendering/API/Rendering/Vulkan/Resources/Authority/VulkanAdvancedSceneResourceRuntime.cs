using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Lowers exact retained canonical publications into immutable frame-slot
/// storage plus independent sampled-image and sampler descriptor arrays.
/// </summary>
internal sealed class VulkanAdvancedSceneResourceRuntime
{
    private const uint RequestedDescriptorCapacity = 1024u;
    private const int PublicationCapacityPerFrameSlot = 256;
    private const int ReceiptCapacityPerFrameSlot = 1024;
    private const int SamplerCacheCapacity = 4096;
    private const ulong StorageCapacityPerFrameSlot = 8ul * 1024ul * 1024ul;
    private const uint StorageAlignment = 16u;
    private const uint FallbackTableByteLength = 1024u;
    private const EAdvancedSamplerRecordFlags SupportedSamplerFlags =
        EAdvancedSamplerRecordFlags.UsesMipmaps |
        EAdvancedSamplerRecordFlags.LinearMipmapInterpolation |
        EAdvancedSamplerRecordFlags.NearestMinification |
        EAdvancedSamplerRecordFlags.NearestMagnification |
        EAdvancedSamplerRecordFlags.ComparisonEnabled |
        EAdvancedSamplerRecordFlags.AnisotropyEnabled;

    private readonly object _gate = new();
    private readonly VulkanResourceRuntime _resources;
    private readonly VulkanAdvancedSceneResourceSlot[] _slots;
    private readonly AdvancedSamplerRecord[] _samplerCacheRecords =
        new AdvancedSamplerRecord[SamplerCacheCapacity];
    private readonly Sampler[] _samplerCache = new Sampler[SamplerCacheCapacity];
    private AdvancedTextureRecord[] _textureRecordScratch = [];
    private AdvancedEncodedTextureReference[] _encodedTextureScratch = [];
    private AdvancedEncodedSamplerReference[] _encodedSamplerScratch = [];
    private DescriptorImageInfo[] _imageDescriptorScratch = [];
    private DescriptorImageInfo[] _samplerDescriptorScratch = [];
    private byte[] _samplerValidationScratch = [];
    private VulkanDeviceContext? _device;
    private DescriptorSetLayout _globalDescriptorSetLayout;
    private DescriptorSetLayout _resourceDescriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private int _samplerCacheCount;
    private ulong _nextNativeGeneration;
    private float _maximumSamplerAnisotropy = 1.0f;
    private bool _supportsSamplerAnisotropy;
    private bool _reportedFirstPublication;

    internal VulkanAdvancedSceneResourceRuntime(
        VulkanResourceRuntime resources,
        int frameSlotCount)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        if (frameSlotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSlotCount));

        _slots = new VulkanAdvancedSceneResourceSlot[frameSlotCount];
        for (int index = 0; index < _slots.Length; ++index)
            _slots[index] = new VulkanAdvancedSceneResourceSlot(
                PublicationCapacityPerFrameSlot,
                ReceiptCapacityPerFrameSlot);
    }

    internal bool IsReady { get; private set; }

    internal EVulkanAdvancedSceneResourceFailure AvailabilityFailure { get; private set; }

    internal string AvailabilityReason { get; private set; } =
        "The Vulkan advanced-scene resource runtime has not been initialized.";

    internal uint DescriptorCapacity { get; private set; }

    internal uint GlobalSetIndex
        => VulkanAdvancedSceneProgramBindingContract.GlobalSetIndex;

    internal uint ResourceSetIndex
        => VulkanAdvancedSceneProgramBindingContract.ResourceSetIndex;

    internal DescriptorSetLayout GlobalDescriptorSetLayout
        => _globalDescriptorSetLayout;

    internal DescriptorSetLayout ResourceDescriptorSetLayout
        => _resourceDescriptorSetLayout;

    internal EAdvancedTextureIndirectionMode TextureIndirectionMode
        => IsReady
            ? EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing
            : EAdvancedTextureIndirectionMode.None;

    internal bool TryGetProgramDescriptorSetLayout(
        uint setIndex,
        out DescriptorSetLayout layout)
    {
        layout = setIndex switch
        {
            VulkanAdvancedSceneProgramBindingContract.GlobalSetIndex =>
                _globalDescriptorSetLayout,
            VulkanAdvancedSceneProgramBindingContract.ResourceSetIndex =>
                _resourceDescriptorSetLayout,
            _ => default,
        };
        return IsReady && layout.Handle != 0;
    }

    internal bool TryInitialize(
        VulkanDeviceContext device,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            if (IsReady)
            {
                reason = "Ready";
                return true;
            }

            EVulkanDescriptorBackend backend =
                _resources.Descriptors.ActiveDescriptorBackend;
            if (backend == EVulkanDescriptorBackend.DescriptorHeap)
            {
                return SetUnavailable(
                    EVulkanAdvancedSceneResourceFailure.DescriptorHeapUnsupported,
                    "Advanced-scene descriptor-heap realization is explicitly unsupported; descriptor indexing is the portable implementation.",
                    out reason);
            }
            if (backend != EVulkanDescriptorBackend.DescriptorIndexing)
            {
                return SetUnavailable(
                    EVulkanAdvancedSceneResourceFailure.DescriptorIndexingUnavailable,
                    $"Advanced-scene realization requires descriptor indexing, but the active backend is {backend}.",
                    out reason);
            }
            if (_resources.FrameDataArena is not { IsActive: true } arena)
            {
                return SetUnavailable(
                    EVulkanAdvancedSceneResourceFailure.RuntimeUnavailable,
                    "The canonical Vulkan frame-data arena is unavailable.",
                    out reason);
            }
            if (!arena.TryReserveLaneCapacity(
                    EVulkanFrameDataLane.AdvancedSceneStorage,
                    StorageCapacityPerFrameSlot,
                    StorageAlignment))
            {
                return SetUnavailable(
                    EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity,
                    $"Failed to reserve {StorageCapacityPerFrameSlot} bytes per frame slot for advanced-scene storage.",
                    out reason);
            }

            try
            {
                device.Api.GetPhysicalDeviceProperties(
                    device.PhysicalDevice,
                    out PhysicalDeviceProperties properties);
                DescriptorCapacity = ResolveDescriptorCapacity(properties);
                if (DescriptorCapacity <= 1u)
                {
                    return SetUnavailable(
                        EVulkanAdvancedSceneResourceFailure.DescriptorIndexingUnavailable,
                        "Vulkan descriptor limits do not leave room for a fallback plus one advanced-scene resource.",
                        out reason);
                }

                _maximumSamplerAnisotropy =
                    MathF.Max(1.0f, properties.Limits.MaxSamplerAnisotropy);
                _supportsSamplerAnisotropy =
                    device.Capabilities.Supports(
                        EVulkanDeviceCapability.Anisotropy) &&
                    _maximumSamplerAnisotropy > 1.0f;
                AllocateScratch(DescriptorCapacity);
                _device = device;
                if (!TryCreateDescriptorStorage(device, out reason))
                {
                    AvailabilityFailure =
                        EVulkanAdvancedSceneResourceFailure.NativeFault;
                    AvailabilityReason = reason;
                    RetireNativeStorageNoLock();
                    return false;
                }

                IsReady = true;
                AvailabilityFailure =
                    EVulkanAdvancedSceneResourceFailure.None;
                AvailabilityReason = "Ready";
                Debug.Vulkan(
                    "[VulkanAdvancedScene] Descriptor-indexing resource runtime ready: frameSlots={0}, descriptorCapacity={1}, globalSet={2}, resourceSet={3}, storageBytesPerSlot={4}.",
                    _slots.Length,
                    DescriptorCapacity,
                    GlobalSetIndex,
                    ResourceSetIndex,
                    StorageCapacityPerFrameSlot);
                reason = "Ready";
                return true;
            }
            catch (Exception exception)
            {
                IsReady = false;
                AvailabilityFailure =
                    EVulkanAdvancedSceneResourceFailure.NativeFault;
                AvailabilityReason =
                    $"Advanced-scene native initialization failed: {exception.Message}";
                RetireNativeStorageNoLock();
                reason = AvailabilityReason;
                return false;
            }
        }
    }

    internal bool TryPreparePublication(
        int frameSlot,
        ulong frameGeneration,
        AdvancedSharedGpuSceneDatabase database,
        in AdvancedGpuScenePublicationReference publication,
        out VulkanAdvancedScenePublicationUse use,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(database);
        use = default;
        lock (_gate)
        {
            if (!IsReady || _device is not { IsOperational: true })
            {
                failure = AvailabilityFailure ==
                    EVulkanAdvancedSceneResourceFailure.None
                        ? EVulkanAdvancedSceneResourceFailure.RuntimeUnavailable
                        : AvailabilityFailure;
                reason = AvailabilityReason;
                return false;
            }
            if ((uint)frameSlot >= (uint)_slots.Length || frameGeneration == 0u)
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.InvalidFrameOwner;
                reason = "The prepared recording has no valid frame-slot generation.";
                return false;
            }
            if (!publication.IsValid ||
                publication.DatabaseEpoch != database.DatabaseEpoch)
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.InvalidPublication;
                reason = "The canonical publication identity is invalid for this database.";
                return false;
            }

            VulkanAdvancedSceneResourceSlot slot = _slots[frameSlot];
            if (slot.Quarantined)
            {
                failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
                reason = "The advanced-scene descriptor set for this frame slot is quarantined after a native publication fault.";
                return false;
            }
            if (slot.FrameGeneration != frameGeneration)
            {
                if (slot.ActiveUseCount != 0)
                {
                    failure =
                        EVulkanAdvancedSceneResourceFailure.FrameSlotStillInUse;
                    reason = "The frame slot still owns advanced-scene publication uses from an incomplete generation.";
                    return false;
                }

                slot.BeginGeneration(frameGeneration);
            }

            int existingIndex = slot.Find(database, publication);
            if (existingIndex >= 0)
                return TryArmUse(
                    slot,
                    frameSlot,
                    existingIndex,
                    out use,
                    out failure,
                    out reason);

            if (slot.EntryCount >= slot.Entries.Length)
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.PublicationCapacity;
                reason = $"Frame slot {frameSlot} exhausted its {slot.Entries.Length} distinct advanced-scene publications.";
                return false;
            }
            if (slot.ReceiptCount >= slot.ReceiptStates.Length)
            {
                failure = EVulkanAdvancedSceneResourceFailure.ReceiptCapacity;
                reason = $"Frame slot {frameSlot} exhausted its {slot.ReceiptStates.Length} advanced-scene publication receipts.";
                return false;
            }
            if (!database.TryGetPublicationSnapshot(
                    publication,
                    out AdvancedGpuScenePublicationSnapshot snapshot) ||
                snapshot.ResourcePayloads.Sequence != publication.Sequence ||
                snapshot.MaterialPayloads.Sequence != publication.Sequence)
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.PublicationSnapshotUnavailable;
                reason = "The exact retained canonical publication snapshot is unavailable or sequence-mismatched.";
                return false;
            }
            if (!snapshot.ResourcePayloads.HasCompleteSourceImage)
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.IncompleteSourceImage;
                reason = "The retained publication does not own a complete strong texture-source image.";
                return false;
            }

            if (!TryBuildPublication(
                    slot,
                    frameSlot,
                    frameGeneration,
                    snapshot,
                    out VulkanAdvancedScenePublicationState state,
                    out failure,
                    out reason))
            {
                return false;
            }

            int entryIndex = slot.EntryCount;
            try
            {
                ref VulkanAdvancedScenePublicationEntry entry =
                    ref slot.Entries[entryIndex];
                entry.Database = database;
                entry.Publication = publication;
                entry.State = state;
                entry.ActiveUseCount = 0;
                ++slot.EntryCount;
                slot.NextTextureDescriptor = checked(
                    state.TextureDescriptorBase +
                    state.TextureDescriptorCount);
                slot.NextSamplerDescriptor = checked(
                    state.SamplerDescriptorBase +
                    state.SamplerDescriptorCount);
            }
            catch
            {
                slot.Quarantined = true;
                failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
                reason = "Advanced-scene state publication faulted after the native descriptor update; the frame-slot set was quarantined.";
                return false;
            }

            bool armed = TryArmUse(
                slot,
                frameSlot,
                entryIndex,
                out use,
                out failure,
                out reason);
            if (armed && !_reportedFirstPublication)
            {
                _reportedFirstPublication = true;
                Debug.Vulkan(
                    "[VulkanAdvancedScene] Lowered first retained canonical publication: sequence={0}, nativeGeneration={1}, frameSlot={2}, textures={3}, samplers={4}.",
                    publication.Sequence,
                    state.NativeGeneration,
                    frameSlot,
                    state.TextureDescriptorCount,
                    state.SamplerDescriptorCount);
            }

            return armed;
        }
    }

    internal void ReleaseUse(int frameSlot, int entryIndex)
    {
        lock (_gate)
        {
            if ((uint)frameSlot >= (uint)_slots.Length)
                return;

            VulkanAdvancedSceneResourceSlot slot = _slots[frameSlot];
            if ((uint)entryIndex >= (uint)slot.EntryCount)
                return;

            ref VulkanAdvancedScenePublicationEntry entry =
                ref slot.Entries[entryIndex];
            if (entry.ActiveUseCount <= 0 || slot.ActiveUseCount <= 0)
                return;

            --entry.ActiveUseCount;
            --slot.ActiveUseCount;
        }
    }

    internal void RetireAll()
    {
        lock (_gate)
        {
            IsReady = false;
            AvailabilityFailure =
                EVulkanAdvancedSceneResourceFailure.RuntimeUnavailable;
            AvailabilityReason =
                "The Vulkan advanced-scene resource runtime is retired.";
            RetireNativeStorageNoLock();
        }
    }

    private bool TryBuildPublication(
        VulkanAdvancedSceneResourceSlot slot,
        int frameSlot,
        ulong frameGeneration,
        AdvancedGpuScenePublicationSnapshot snapshot,
        out VulkanAdvancedScenePublicationState state,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        state = default;
        AdvancedGpuRecordTablePublicationSnapshot<AdvancedTextureRecord>
            textures = snapshot.Textures;
        AdvancedGpuRecordTablePublicationSnapshot<AdvancedSamplerRecord>
            samplers = snapshot.Samplers;
        int textureHighWater = textures.PhysicalRecords.Length;
        int samplerHighWater = samplers.PhysicalRecords.Length;
        if (textureHighWater + 1 > _textureRecordScratch.Length ||
            textureHighWater + 1 > _encodedTextureScratch.Length)
        {
            failure =
                EVulkanAdvancedSceneResourceFailure.TextureDescriptorCapacity;
            reason = $"Publication texture high-water {textureHighWater} exceeds the native descriptor capacity {DescriptorCapacity - 1u}.";
            return false;
        }
        if (samplerHighWater + 1 > _encodedSamplerScratch.Length ||
            samplerHighWater > _samplerValidationScratch.Length)
        {
            failure =
                EVulkanAdvancedSceneResourceFailure.SamplerDescriptorCapacity;
            reason = $"Publication sampler high-water {samplerHighWater} exceeds the native descriptor capacity {DescriptorCapacity - 1u}.";
            return false;
        }

        uint textureBase = slot.NextTextureDescriptor;
        uint samplerBase = slot.NextSamplerDescriptor;
        if ((ulong)textureBase + (uint)textureHighWater >
            DescriptorCapacity)
        {
            failure =
                EVulkanAdvancedSceneResourceFailure.TextureDescriptorCapacity;
            reason = $"Frame-slot sampled-image table capacity {DescriptorCapacity} cannot append {textureHighWater} rows at {textureBase}.";
            return false;
        }
        if ((ulong)samplerBase + (uint)samplerHighWater >
            DescriptorCapacity)
        {
            failure =
                EVulkanAdvancedSceneResourceFailure.SamplerDescriptorCapacity;
            reason = $"Frame-slot sampler table capacity {DescriptorCapacity} cannot append {samplerHighWater} rows at {samplerBase}.";
            return false;
        }
        if (!TryValidatePublicationSources(snapshot, out failure, out reason))
            return false;

        DescriptorImageInfo fallback =
            _resources.FallbackTexture.GetImageInfo(
                DescriptorType.CombinedImageSampler,
                ImageViewType.Type2D);
        if (fallback.ImageView.Handle == 0 || fallback.Sampler.Handle == 0)
        {
            failure = EVulkanAdvancedSceneResourceFailure.RuntimeUnavailable;
            reason = "The Vulkan fallback sampled image and sampler are unavailable.";
            return false;
        }

        if (!TryPrepareSamplerRows(
                samplers,
                samplerBase,
                fallback,
                out failure,
                out reason) ||
            !TryPrepareTextureRows(
                snapshot,
                textures,
                samplers,
                textureBase,
                samplerBase,
                fallback,
                out failure,
                out reason))
        {
            return false;
        }

        ulong requiredStorage = CalculatePublicationStorageBytes(snapshot);
        if (requiredStorage > StorageCapacityPerFrameSlot ||
            slot.StorageBytesConsumed >
                StorageCapacityPerFrameSlot - requiredStorage)
        {
            failure =
                EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            reason = $"Frame-slot advanced-scene storage requires {requiredStorage} more bytes after {slot.StorageBytesConsumed} of {StorageCapacityPerFrameSlot} bytes were consumed.";
            return false;
        }

        // Arena writes consume offsets even when a later native descriptor
        // update is rejected. Reserve the complete transaction conservatively
        // so retries cannot trigger hidden in-frame growth.
        slot.StorageBytesConsumed += requiredStorage;
        if (!TryUploadPublicationTables(
                frameSlot,
                snapshot,
                textureHighWater,
                samplerHighWater,
                out VulkanFrameDataSlice materialSlice,
                out VulkanFrameDataSlice kernelSlice,
                out VulkanFrameDataSlice layoutSlice,
                out VulkanFrameDataSlice constantSlice,
                out VulkanFrameDataSlice bindingSlice,
                out VulkanFrameDataSlice textureSlice,
                out VulkanFrameDataSlice samplerSlice,
                out VulkanFrameDataSlice encodedTextureSlice,
                out VulkanFrameDataSlice encodedSamplerSlice,
                out VulkanFrameDataSlice lookupSlice,
                out VulkanFrameDataSlice fallbackTableSlice,
                out VulkanAdvancedSceneLookupSegments lookupSegments))
        {
            failure =
                EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            reason = "The boundary-reserved advanced-scene storage lane could not publish the complete immutable table image.";
            return false;
        }

        DescriptorSet globalDescriptorSet =
            slot.GlobalDescriptorSets[slot.EntryCount];
        if (!TryUpdateGlobalTableDescriptors(
                globalDescriptorSet,
                fallbackTableSlice,
                materialSlice,
                kernelSlice,
                layoutSlice,
                constantSlice,
                bindingSlice,
                textureSlice,
                samplerSlice,
                encodedTextureSlice,
                encodedSamplerSlice,
                lookupSlice,
                out reason) ||
            !TryUpdateDescriptorRanges(
                slot.ResourceDescriptorSet,
                textureBase,
                (uint)textureHighWater,
                samplerBase,
                (uint)samplerHighWater,
                out reason))
        {
            failure =
                EVulkanAdvancedSceneResourceFailure.DescriptorUpdateFailed;
            return false;
        }

        ulong nativeGeneration = NextNativeGeneration();
        state = new VulkanAdvancedScenePublicationState(
            frameSlot,
            frameGeneration,
            nativeGeneration,
            globalDescriptorSet,
            slot.ResourceDescriptorSet,
            textureBase,
            (uint)textureHighWater,
            samplerBase,
            (uint)samplerHighWater,
            materialSlice,
            kernelSlice,
            layoutSlice,
            constantSlice,
            bindingSlice,
            textureSlice,
            samplerSlice,
            encodedTextureSlice,
            encodedSamplerSlice,
            lookupSlice,
            fallbackTableSlice,
            lookupSegments);
        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = "Ready";
        return true;
    }

    private bool TryValidatePublicationSources(
        AdvancedGpuScenePublicationSnapshot snapshot,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        AdvancedGpuResourcePublicationSnapshot resources =
            snapshot.ResourcePayloads;
        ReadOnlySpan<AdvancedTextureRecord> textureRecords =
            snapshot.Textures.PhysicalRecords;
        ReadOnlySpan<AdvancedGpuHandle> textureHandles =
            snapshot.Textures.PhysicalHandles;
        ReadOnlySpan<byte> textureOccupancy =
            snapshot.Textures.PhysicalOccupancy;
        for (int denseIndex = 0; denseIndex < textureRecords.Length; ++denseIndex)
        {
            if (textureOccupancy[denseIndex] == 0)
                continue;

            AdvancedGpuHandle handle = textureHandles[denseIndex];
            AdvancedTextureRecord canonical = textureRecords[denseIndex];
            if (!handle.IsValid || canonical.StableTextureId != handle.Index ||
                canonical.Generation != handle.Generation ||
                canonical.EncodedReferenceIndex != 0u)
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.SourceMismatch;
                reason = $"Canonical texture row {denseIndex} has inconsistent handle metadata for {handle.Index}:{handle.Generation}.";
                return false;
            }
            if (!resources.TryGetTextureSource(handle, out XRTexture source))
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.SourceMismatch;
                reason = $"Canonical texture {handle.Index}:{handle.Generation} has no retained strong source reference.";
                return false;
            }
            if (!AdvancedGpuResourceSourceEncoder.TryEncode(
                    source,
                    EAdvancedResourceFallback.Zero,
                    out AdvancedGpuResourceBindingSource current,
                    out _,
                    out string encodeReason))
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.SourceMismatch;
                reason = $"Canonical texture {handle.Index}:{handle.Generation} source revalidation failed: {encodeReason}";
                return false;
            }
            if (!TextureStateEquals(current.TextureRecord, canonical))
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.SourceMismatch;
                reason = $"Canonical texture {handle.Index}:{handle.Generation} source '{source.Name ?? source.GetType().Name}' changed after publication: {DescribeTextureStateMismatch(current.TextureRecord, canonical)}";
                return false;
            }
        }

        int samplerHighWater = snapshot.Samplers.PhysicalRecords.Length;
        _samplerValidationScratch.AsSpan(0, samplerHighWater).Clear();
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
                failure =
                    EVulkanAdvancedSceneResourceFailure.SourceMismatch;
                reason = $"Material texture binding {bindingIndex} does not resolve its retained texture/sampler publication.";
                return false;
            }
            if (!AdvancedGpuResourceSourceEncoder.TryEncode(
                    source,
                    binding.Texture.Fallback,
                    out AdvancedGpuResourceBindingSource current,
                    out _,
                    out string encodeReason))
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.SourceMismatch;
                reason = $"Material texture binding {bindingIndex} source revalidation failed: {encodeReason}";
                return false;
            }
            if (!TextureStateEquals(current.TextureRecord, canonicalTexture) ||
                !SamplerStateEquals(current.SamplerRecord, canonicalSampler))
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.SourceMismatch;
                reason = $"Material texture binding {bindingIndex} source '{source.Name ?? source.GetType().Name}' changed after publication: texture=({DescribeTextureStateMismatch(current.TextureRecord, canonicalTexture)}), sampler=({DescribeSamplerStateMismatch(current.SamplerRecord, canonicalSampler)})";
                return false;
            }
            if (!snapshot.Samplers.TryGetDenseIndex(
                    binding.Sampler.Handle,
                    out uint samplerDense))
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.SourceMismatch;
                reason = $"Material texture binding {bindingIndex} sampler handle is absent from the retained dense image.";
                return false;
            }

            _samplerValidationScratch[checked((int)samplerDense)] = 1;
        }

        ReadOnlySpan<byte> samplerOccupancy =
            snapshot.Samplers.PhysicalOccupancy;
        for (int denseIndex = 0; denseIndex < samplerHighWater; ++denseIndex)
            if (samplerOccupancy[denseIndex] != 0 &&
                _samplerValidationScratch[denseIndex] == 0)
            {
                AdvancedGpuHandle handle =
                    snapshot.Samplers.PhysicalHandles[denseIndex];
                failure = EVulkanAdvancedSceneResourceFailure.SourceMismatch;
                reason = $"Canonical sampler {handle.Index}:{handle.Generation} has no retained material binding that can revalidate its source state.";
                return false;
            }

        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = "Ready";
        return true;
    }

    private bool TryPrepareTextureRows(
        AdvancedGpuScenePublicationSnapshot snapshot,
        AdvancedGpuRecordTablePublicationSnapshot<AdvancedTextureRecord> textures,
        AdvancedGpuRecordTablePublicationSnapshot<AdvancedSamplerRecord> samplers,
        uint textureBase,
        uint samplerBase,
        in DescriptorImageInfo fallback,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        ReadOnlySpan<AdvancedTextureRecord> records = textures.PhysicalRecords;
        records.CopyTo(_textureRecordScratch);
        _encodedTextureScratch[0] = CreateFallbackTextureReference();
        for (int denseIndex = 0; denseIndex < records.Length; ++denseIndex)
        {
            _imageDescriptorScratch[denseIndex] = new DescriptorImageInfo
            {
                ImageView = fallback.ImageView,
                ImageLayout = fallback.ImageLayout,
            };
            _encodedTextureScratch[denseIndex + 1] =
                CreateFallbackTextureReference();
            if (textures.PhysicalOccupancy[denseIndex] == 0)
                continue;

            AdvancedGpuHandle handle = textures.PhysicalHandles[denseIndex];
            AdvancedTextureRecord record = records[denseIndex];
            if (!handle.IsValid || record.StableTextureId != handle.Index ||
                record.Generation != handle.Generation ||
                record.Dimension != EAdvancedTextureDimension.Texture2D ||
                record.DepthOrLayers != 1u)
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.UnsupportedTextureShape;
                reason = $"Canonical texture row {denseIndex} is not a valid single-layer Texture2D record.";
                return false;
            }
            if (!snapshot.ResourcePayloads.TryGetTextureSource(
                    handle,
                    out XRTexture source))
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.IncompleteSourceImage;
                reason = $"Canonical texture {handle.Index}:{handle.Generation} has no strong retained source.";
                return false;
            }
            if (_resources.BackendObjects.Get(source) is not
                    IVkImageDescriptorSource descriptorSource)
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.TextureWrapperUnavailable;
                reason = $"Canonical texture {handle.Index}:{handle.Generation} has no existing Vulkan image wrapper; synchronous generation is forbidden during frame preparation.";
                return false;
            }

            ImageAspectFlags? aspect =
                (record.Flags & EAdvancedTextureRecordFlags.Depth) != 0
                    ? ImageAspectFlags.DepthBit
                    : null;
            if (!descriptorSource.TryGetDescriptorSnapshot(
                    ImageViewType.Type2D,
                    aspect,
                    "Advanced canonical scene publication",
                    allowSynchronousUpload: false,
                    out VkImageDescriptorSnapshot descriptor) ||
                !descriptor.IsReady || descriptor.View.Handle == 0 ||
                descriptor.ViewType != ImageViewType.Type2D ||
                descriptor.Samples != SampleCountFlags.Count1Bit ||
                descriptor.ArrayLayers != 1u ||
                !_resources.Images.IsAvailableForDescriptor(descriptor.View))
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.TextureDescriptorNotReady;
                reason = $"Canonical texture {handle.Index}:{handle.Generation} is not ready for an exact Vulkan sampled-image descriptor.";
                return false;
            }

            uint defaultSamplerDescriptor = 0u;
            if (record.DefaultSampler.IsValid &&
                samplers.TryGetDenseIndex(
                    record.DefaultSampler,
                    out uint defaultSamplerDense))
            {
                defaultSamplerDescriptor = checked(
                    samplerBase + defaultSamplerDense);
            }

            record.EncodedReferenceIndex = checked((uint)denseIndex + 1u);
            _textureRecordScratch[denseIndex] = record;
            _imageDescriptorScratch[denseIndex] = new DescriptorImageInfo
            {
                ImageView = descriptor.View,
                ImageLayout = descriptor.TrackedLayout == ImageLayout.Undefined
                    ? ImageLayout.ShaderReadOnlyOptimal
                    : descriptor.TrackedLayout,
            };
            _encodedTextureScratch[denseIndex + 1] =
                new AdvancedEncodedTextureReference(
                    checked(textureBase + (uint)denseIndex),
                    defaultSamplerDescriptor,
                    0u,
                    EAdvancedResourceReferenceFlags.Resident);
        }

        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = "Ready";
        return true;
    }

    private bool TryPrepareSamplerRows(
        AdvancedGpuRecordTablePublicationSnapshot<AdvancedSamplerRecord> samplers,
        uint samplerBase,
        in DescriptorImageInfo fallback,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        _encodedSamplerScratch[0] = CreateFallbackSamplerReference();
        ReadOnlySpan<AdvancedSamplerRecord> records = samplers.PhysicalRecords;
        for (int denseIndex = 0; denseIndex < records.Length; ++denseIndex)
        {
            _samplerDescriptorScratch[denseIndex] = new DescriptorImageInfo
            {
                Sampler = fallback.Sampler,
            };
            _encodedSamplerScratch[denseIndex + 1] =
                CreateFallbackSamplerReference();
            if (samplers.PhysicalOccupancy[denseIndex] == 0)
                continue;

            AdvancedGpuHandle handle = samplers.PhysicalHandles[denseIndex];
            AdvancedSamplerRecord record = records[denseIndex];
            if (!handle.IsValid || record.StableSamplerId != handle.Index ||
                record.Generation != handle.Generation)
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.UnsupportedSamplerState;
                reason = $"Canonical sampler row {denseIndex} has invalid stable identity.";
                return false;
            }
            if (!TryGetOrCreateSampler(
                    record,
                    out Sampler sampler,
                    out failure,
                    out reason))
            {
                return false;
            }

            _samplerDescriptorScratch[denseIndex] =
                new DescriptorImageInfo { Sampler = sampler };
            _encodedSamplerScratch[denseIndex + 1] =
                new AdvancedEncodedSamplerReference(
                    checked(samplerBase + (uint)denseIndex),
                    0u,
                    0u,
                    EAdvancedResourceReferenceFlags.Resident);
        }

        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = "Ready";
        return true;
    }

    private bool TryUploadPublicationTables(
        int frameSlot,
        AdvancedGpuScenePublicationSnapshot snapshot,
        int textureHighWater,
        int samplerHighWater,
        out VulkanFrameDataSlice materials,
        out VulkanFrameDataSlice kernels,
        out VulkanFrameDataSlice layouts,
        out VulkanFrameDataSlice constants,
        out VulkanFrameDataSlice bindings,
        out VulkanFrameDataSlice textures,
        out VulkanFrameDataSlice samplers,
        out VulkanFrameDataSlice encodedTextures,
        out VulkanFrameDataSlice encodedSamplers,
        out VulkanFrameDataSlice lookups,
        out VulkanFrameDataSlice fallbackTable,
        out VulkanAdvancedSceneLookupSegments lookupSegments)
    {
        materials = default;
        kernels = default;
        layouts = default;
        constants = default;
        bindings = default;
        textures = default;
        samplers = default;
        encodedTextures = default;
        encodedSamplers = default;
        lookups = default;
        fallbackTable = default;
        lookupSegments = default;
        AdvancedMaterialPublicationSnapshot material =
            snapshot.MaterialPayloads;
        return TryUploadFallbackTable(frameSlot, out fallbackTable) &&
               TryUpload(
                   frameSlot,
                   material.Materials.PhysicalRecords,
                   out materials) &&
               TryUpload(frameSlot, material.Kernels.PhysicalRecords, out kernels) &&
               TryUpload(frameSlot, material.Layouts.PhysicalRecords, out layouts) &&
               TryUpload(frameSlot, material.ConstantWords, out constants) &&
               TryUpload(frameSlot, material.TextureBindings, out bindings) &&
               TryUpload(
                   frameSlot,
                   _textureRecordScratch.AsSpan(0, textureHighWater),
                   out textures) &&
               TryUpload(frameSlot, snapshot.Samplers.PhysicalRecords, out samplers) &&
               TryUpload(
                   frameSlot,
                   _encodedTextureScratch.AsSpan(0, textureHighWater + 1),
                   out encodedTextures) &&
               TryUpload(
                   frameSlot,
                   _encodedSamplerScratch.AsSpan(0, samplerHighWater + 1),
                   out encodedSamplers) &&
               TryUploadLookups(
                   frameSlot,
                   material.Materials.HandleLookups,
                   material.Kernels.HandleLookups,
                   material.Layouts.HandleLookups,
                   snapshot.Textures.HandleLookups,
                   snapshot.Samplers.HandleLookups,
                   out lookups,
                   out lookupSegments);
    }

    private bool TryUploadFallbackTable(
        int frameSlot,
        out VulkanFrameDataSlice slice)
    {
        VulkanFrameDataArena arena = _resources.FrameDataArena!;
        if (!arena.TryAllocate(
                frameSlot,
                EVulkanFrameDataLane.AdvancedSceneStorage,
                FallbackTableByteLength,
                StorageAlignment,
                out slice) ||
            !arena.TryBeginWrite(slice, out VulkanFrameDataWriteScope write))
        {
            slice = default;
            return false;
        }

        using (write)
            write.Bytes.Clear();
        return true;
    }

    private bool TryUpload<T>(
        int frameSlot,
        ReadOnlySpan<T> source,
        out VulkanFrameDataSlice slice)
        where T : unmanaged
    {
        T sentinel = default;
        ReadOnlySpan<T> rows = source.IsEmpty
            ? MemoryMarshal.CreateReadOnlySpan(ref sentinel, 1)
            : source;
        return _resources.FrameDataArena!.TryAllocateWrite(
            frameSlot,
            EVulkanFrameDataLane.AdvancedSceneStorage,
            MemoryMarshal.AsBytes(rows),
            StorageAlignment,
            out slice);
    }

    private bool TryUploadLookups(
        int frameSlot,
        ReadOnlySpan<AdvancedGpuHandleLookup> materials,
        ReadOnlySpan<AdvancedGpuHandleLookup> kernels,
        ReadOnlySpan<AdvancedGpuHandleLookup> layouts,
        ReadOnlySpan<AdvancedGpuHandleLookup> textures,
        ReadOnlySpan<AdvancedGpuHandleLookup> samplers,
        out VulkanFrameDataSlice slice,
        out VulkanAdvancedSceneLookupSegments segments)
    {
        uint materialOffset = 0u;
        uint kernelOffset = checked(materialOffset + (uint)materials.Length);
        uint layoutOffset = checked(kernelOffset + (uint)kernels.Length);
        uint textureOffset = checked(layoutOffset + (uint)layouts.Length);
        uint samplerOffset = checked(textureOffset + (uint)textures.Length);
        uint total = checked(samplerOffset + (uint)samplers.Length);
        if (total == 0u)
            total = 1u;

        segments = new VulkanAdvancedSceneLookupSegments(
            new AdvancedGpuLookupSegment(materialOffset, (uint)materials.Length),
            new AdvancedGpuLookupSegment(kernelOffset, (uint)kernels.Length),
            new AdvancedGpuLookupSegment(layoutOffset, (uint)layouts.Length),
            new AdvancedGpuLookupSegment(textureOffset, (uint)textures.Length),
            new AdvancedGpuLookupSegment(samplerOffset, (uint)samplers.Length));
        VulkanFrameDataArena arena = _resources.FrameDataArena!;
        if (!arena.TryAllocate(
                frameSlot,
                EVulkanFrameDataLane.AdvancedSceneStorage,
                checked(total * (uint)Unsafe.SizeOf<AdvancedGpuHandleLookup>()),
                StorageAlignment,
                out slice) ||
            !arena.TryBeginWrite(slice, out VulkanFrameDataWriteScope write))
        {
            slice = default;
            return false;
        }

        using (write)
        {
            Span<AdvancedGpuHandleLookup> destination =
                MemoryMarshal.Cast<byte, AdvancedGpuHandleLookup>(write.Bytes);
            destination.Fill(AdvancedGpuHandleLookup.Invalid);
            materials.CopyTo(destination[(int)materialOffset..]);
            kernels.CopyTo(destination[(int)kernelOffset..]);
            layouts.CopyTo(destination[(int)layoutOffset..]);
            textures.CopyTo(destination[(int)textureOffset..]);
            samplers.CopyTo(destination[(int)samplerOffset..]);
        }
        return true;
    }

    private unsafe bool TryGetOrCreateSampler(
        in AdvancedSamplerRecord record,
        out Sampler sampler,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        for (int index = 0; index < _samplerCacheCount; ++index)
            if (SamplerStateEquals(_samplerCacheRecords[index], record))
            {
                sampler = _samplerCache[index];
                failure = EVulkanAdvancedSceneResourceFailure.None;
                reason = "Ready";
                return true;
            }

        sampler = default;
        if (!TryCreateSamplerInfo(
                record,
                out SamplerCreateInfo createInfo,
                out failure,
                out reason))
        {
            return false;
        }
        if (_samplerCacheCount >= _samplerCache.Length)
        {
            failure =
                EVulkanAdvancedSceneResourceFailure.SamplerCacheCapacity;
            reason = $"The bounded Vulkan advanced-scene sampler cache exhausted {_samplerCache.Length} semantic states.";
            return false;
        }

        VulkanDeviceContext device = _device!;
        Result result = device.Api.CreateSampler(
            device.Device,
            ref createInfo,
            null,
            out sampler);
        if (result != Result.Success)
        {
            failure = result == Result.ErrorDeviceLost
                ? EVulkanAdvancedSceneResourceFailure.DeviceLost
                : EVulkanAdvancedSceneResourceFailure.NativeSamplerCreationFailed;
            reason = $"vkCreateSampler failed for an advanced-scene sampler ({result}).";
            sampler = default;
            return false;
        }

        try
        {
            _resources.Samplers.Register(
                sampler,
                createInfo,
                "AdvancedScene.SamplerCache");
        }
        catch
        {
            device.Api.DestroySampler(device.Device, sampler, null);
            sampler = default;
            failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
            reason = "The advanced-scene sampler could not be registered with Vulkan lifetime authority.";
            return false;
        }

        int cacheIndex = _samplerCacheCount++;
        _samplerCacheRecords[cacheIndex] = record;
        _samplerCache[cacheIndex] = sampler;
        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = "Ready";
        return true;
    }

    private bool TryCreateSamplerInfo(
        in AdvancedSamplerRecord record,
        out SamplerCreateInfo createInfo,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        createInfo = default;
        if ((record.Flags & ~SupportedSamplerFlags) != 0 ||
            !IsKnownFilter(record.Filter) ||
            !TryMapAddress(record.AddressU, out SamplerAddressMode addressU) ||
            !TryMapAddress(record.AddressV, out SamplerAddressMode addressV) ||
            !TryMapAddress(record.AddressW, out SamplerAddressMode addressW) ||
            !TryMapCompare(record.CompareOperation, out CompareOp compare) ||
            !IsFinite(record.LodBiasMinMaxAnisotropy) ||
            record.LodBiasMinMaxAnisotropy.Y < 0.0f ||
            record.LodBiasMinMaxAnisotropy.Z <
                record.LodBiasMinMaxAnisotropy.Y ||
            record.BorderColor != new Vector4(0.0f, 0.0f, 0.0f, 1.0f))
        {
            failure =
                EVulkanAdvancedSceneResourceFailure.UnsupportedSamplerState;
            reason = "The canonical sampler record contains unsupported or non-finite Vulkan state.";
            return false;
        }

        bool anisotropy =
            (record.Flags & EAdvancedSamplerRecordFlags.AnisotropyEnabled) != 0 ||
            record.Filter == EAdvancedSamplerFilter.Anisotropic;
        float requestedAnisotropy =
            record.LodBiasMinMaxAnisotropy.W;
        if (anisotropy &&
            (!_supportsSamplerAnisotropy || requestedAnisotropy <= 1.0f))
        {
            failure =
                EVulkanAdvancedSceneResourceFailure.UnsupportedSamplerState;
            reason = "The canonical sampler requests anisotropy that the selected Vulkan device cannot realize exactly.";
            return false;
        }

        bool comparison =
            (record.Flags & EAdvancedSamplerRecordFlags.ComparisonEnabled) != 0;
        createInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter =
                (record.Flags & EAdvancedSamplerRecordFlags.NearestMagnification) != 0
                    ? Filter.Nearest
                    : Filter.Linear,
            MinFilter =
                (record.Flags & EAdvancedSamplerRecordFlags.NearestMinification) != 0
                    ? Filter.Nearest
                    : Filter.Linear,
            MipmapMode =
                (record.Flags & EAdvancedSamplerRecordFlags.LinearMipmapInterpolation) != 0
                    ? SamplerMipmapMode.Linear
                    : SamplerMipmapMode.Nearest,
            AddressModeU = addressU,
            AddressModeV = addressV,
            AddressModeW = addressW,
            MipLodBias = record.LodBiasMinMaxAnisotropy.X,
            AnisotropyEnable = anisotropy ? Vk.True : Vk.False,
            MaxAnisotropy = anisotropy
                ? MathF.Min(requestedAnisotropy, _maximumSamplerAnisotropy)
                : 1.0f,
            CompareEnable = comparison ? Vk.True : Vk.False,
            CompareOp = compare,
            MinLod = record.LodBiasMinMaxAnisotropy.Y,
            MaxLod = record.LodBiasMinMaxAnisotropy.Z,
            BorderColor = BorderColor.FloatOpaqueBlack,
            UnnormalizedCoordinates = Vk.False,
        };
        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = "Ready";
        return true;
    }

    private unsafe bool TryCreateDescriptorStorage(
        VulkanDeviceContext device,
        out string reason)
    {
        ShaderStageFlags stages =
            ShaderStageFlags.VertexBit |
            ShaderStageFlags.FragmentBit |
            ShaderStageFlags.ComputeBit;
        ReadOnlySpan<uint> globalBindingNumbers =
            VulkanAdvancedSceneProgramBindingContract.RequiredGlobalStorageBindings;
        DescriptorSetLayoutBinding* globalBindings =
            stackalloc DescriptorSetLayoutBinding[globalBindingNumbers.Length];
        for (int index = 0; index < globalBindingNumbers.Length; ++index)
        {
            globalBindings[index] = new DescriptorSetLayoutBinding
            {
                Binding = globalBindingNumbers[index],
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1u,
                StageFlags = stages,
            };
        }
        DescriptorSetLayoutCreateInfo globalLayoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)globalBindingNumbers.Length,
            PBindings = globalBindings,
        };
        Result result = device.Api.CreateDescriptorSetLayout(
            device.Device,
            ref globalLayoutInfo,
            null,
            out _globalDescriptorSetLayout);
        if (result != Result.Success)
        {
            reason =
                $"Failed to create the advanced-scene global descriptor-set layout ({result}).";
            return false;
        }
        _resources.RegisterDescriptorSetLayout(
            _globalDescriptorSetLayout,
            "AdvancedScene.GlobalDescriptorSetLayout");

        DescriptorSetLayoutBinding* resourceBindings =
            stackalloc DescriptorSetLayoutBinding[2];
        resourceBindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = AdvancedGlobalResourceBindings.TextureDescriptors,
            DescriptorType = DescriptorType.SampledImage,
            DescriptorCount = DescriptorCapacity,
            StageFlags = stages,
        };
        resourceBindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = AdvancedGlobalResourceBindings.SamplerDescriptors,
            DescriptorType = DescriptorType.Sampler,
            DescriptorCount = DescriptorCapacity,
            StageFlags = stages,
        };
        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2u,
            PBindings = resourceBindings,
        };
        result = device.Api.CreateDescriptorSetLayout(
            device.Device,
            ref layoutInfo,
            null,
            out _resourceDescriptorSetLayout);
        if (result != Result.Success)
        {
            reason = $"Failed to create the advanced-scene resource descriptor-set layout ({result}).";
            return false;
        }
        _resources.RegisterDescriptorSetLayout(
            _resourceDescriptorSetLayout,
            "AdvancedScene.ResourceDescriptorSetLayout");

        uint totalResourceDescriptors = checked(
            DescriptorCapacity * (uint)_slots.Length);
        uint globalSetCount = checked(
            (uint)_slots.Length * (uint)PublicationCapacityPerFrameSlot);
        uint totalStorageDescriptors = checked(
            globalSetCount * (uint)globalBindingNumbers.Length);
        DescriptorPoolSize* poolSizes = stackalloc DescriptorPoolSize[3];
        poolSizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.SampledImage,
            DescriptorCount = totalResourceDescriptors,
        };
        poolSizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.Sampler,
            DescriptorCount = totalResourceDescriptors,
        };
        poolSizes[2] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = totalStorageDescriptors,
        };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 3u,
            PPoolSizes = poolSizes,
            MaxSets = checked((uint)_slots.Length + globalSetCount),
        };
        result = device.Api.CreateDescriptorPool(
            device.Device,
            ref poolInfo,
            null,
            out _descriptorPool);
        if (result != Result.Success)
        {
            reason = $"Failed to create the advanced-scene descriptor pool ({result}).";
            return false;
        }
        _resources.Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(
                ObjectType.DescriptorPool,
                _descriptorPool.Handle),
            "AdvancedScene.DescriptorPool",
            externallyOwned: false);

        DescriptorSetLayout[] resourceLayouts =
            new DescriptorSetLayout[_slots.Length];
        DescriptorSet[] resourceDescriptorSets =
            new DescriptorSet[_slots.Length];
        resourceLayouts.AsSpan().Fill(_resourceDescriptorSetLayout);
        fixed (DescriptorSetLayout* layoutPointer = resourceLayouts)
        fixed (DescriptorSet* setPointer = resourceDescriptorSets)
        {
            DescriptorSetAllocateInfo allocation = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = (uint)resourceDescriptorSets.Length,
                PSetLayouts = layoutPointer,
            };
            result = device.Api.AllocateDescriptorSets(
                device.Device,
                ref allocation,
                setPointer);
        }
        if (result != Result.Success)
        {
            reason = $"Failed to allocate per-frame advanced-scene descriptor sets ({result}).";
            return false;
        }
        _resources.DescriptorLifetime.RegisterDescriptorSets(
            _descriptorPool,
            resourceDescriptorSets,
            usesUpdateAfterBind: false,
            owner: "AdvancedScene.ResourceDescriptorSet");
        for (int index = 0; index < _slots.Length; ++index)
            _slots[index].ResourceDescriptorSet = resourceDescriptorSets[index];

        DescriptorSetLayout[] globalLayouts =
            new DescriptorSetLayout[globalSetCount];
        DescriptorSet[] globalDescriptorSets =
            new DescriptorSet[globalSetCount];
        globalLayouts.AsSpan().Fill(_globalDescriptorSetLayout);
        fixed (DescriptorSetLayout* layoutPointer = globalLayouts)
        fixed (DescriptorSet* setPointer = globalDescriptorSets)
        {
            DescriptorSetAllocateInfo allocation = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = globalSetCount,
                PSetLayouts = layoutPointer,
            };
            result = device.Api.AllocateDescriptorSets(
                device.Device,
                ref allocation,
                setPointer);
        }
        if (result != Result.Success)
        {
            reason =
                $"Failed to allocate bounded advanced-scene global descriptor sets ({result}).";
            return false;
        }
        _resources.DescriptorLifetime.RegisterDescriptorSets(
            _descriptorPool,
            globalDescriptorSets,
            usesUpdateAfterBind: false,
            owner: "AdvancedScene.GlobalDescriptorSet");
        int globalSetIndex = 0;
        for (int slotIndex = 0; slotIndex < _slots.Length; ++slotIndex)
            for (int publicationIndex = 0;
                 publicationIndex < PublicationCapacityPerFrameSlot;
                 ++publicationIndex)
            {
                _slots[slotIndex].GlobalDescriptorSets[publicationIndex] =
                    globalDescriptorSets[globalSetIndex++];
            }

        DescriptorImageInfo fallback =
            _resources.FallbackTexture.GetImageInfo(
                DescriptorType.CombinedImageSampler,
                ImageViewType.Type2D);
        if (fallback.ImageView.Handle == 0 || fallback.Sampler.Handle == 0)
        {
            reason = "The fallback texture could not initialize advanced-scene descriptor arrays.";
            return false;
        }
        for (int index = 0; index < (int)DescriptorCapacity; ++index)
        {
            _imageDescriptorScratch[index] = new DescriptorImageInfo
            {
                ImageView = fallback.ImageView,
                ImageLayout = fallback.ImageLayout,
            };
            _samplerDescriptorScratch[index] = new DescriptorImageInfo
            {
                Sampler = fallback.Sampler,
            };
        }
        for (int slotIndex = 0; slotIndex < _slots.Length; ++slotIndex)
            if (!TryUpdateDescriptorRanges(
                    _slots[slotIndex].ResourceDescriptorSet,
                    0u,
                    DescriptorCapacity,
                    0u,
                    DescriptorCapacity,
                    out reason))
            {
                return false;
            }

        _resources.RecordDescriptorTableGeneration();
        reason = "Ready";
        return true;
    }

    private unsafe bool TryUpdateGlobalTableDescriptors(
        DescriptorSet descriptorSet,
        in VulkanFrameDataSlice fallback,
        in VulkanFrameDataSlice materials,
        in VulkanFrameDataSlice kernels,
        in VulkanFrameDataSlice layouts,
        in VulkanFrameDataSlice constants,
        in VulkanFrameDataSlice materialTextureBindings,
        in VulkanFrameDataSlice textures,
        in VulkanFrameDataSlice samplers,
        in VulkanFrameDataSlice encodedTextures,
        in VulkanFrameDataSlice encodedSamplers,
        in VulkanFrameDataSlice handleLookups,
        out string reason)
    {
        ReadOnlySpan<uint> bindings =
            VulkanAdvancedSceneProgramBindingContract.RequiredGlobalStorageBindings;
        DescriptorBufferInfo* bufferInfos =
            stackalloc DescriptorBufferInfo[bindings.Length];
        WriteDescriptorSet* writes =
            stackalloc WriteDescriptorSet[bindings.Length];
        for (int index = 0; index < bindings.Length; ++index)
        {
            uint binding = bindings[index];
            VulkanFrameDataSlice slice = binding switch
            {
                AdvancedGlobalResourceBindings.Materials => materials,
                AdvancedGlobalResourceBindings.Textures => textures,
                AdvancedGlobalResourceBindings.Samplers => samplers,
                AdvancedGlobalResourceBindings.MaterialConstants => constants,
                AdvancedGlobalResourceBindings.MaterialTextureBindings =>
                    materialTextureBindings,
                AdvancedGlobalResourceBindings.EncodedTextures => encodedTextures,
                AdvancedGlobalResourceBindings.EncodedSamplers => encodedSamplers,
                AdvancedGlobalResourceBindings.ShadingKernels => kernels,
                AdvancedGlobalResourceBindings.MaterialLayouts => layouts,
                AdvancedGlobalResourceBindings.HandleLookups => handleLookups,
                _ => fallback,
            };
            if (!slice.IsValid)
            {
                reason =
                    $"Advanced-scene global binding {binding} has no valid frame-data slice.";
                return false;
            }

            bufferInfos[index] = new DescriptorBufferInfo
            {
                Buffer = slice.Buffer,
                Offset = slice.Offset,
                Range = slice.Length,
            };
            writes[index] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = binding,
                DescriptorCount = 1u,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bufferInfos[index],
            };
        }

        if (!_resources.DescriptorLifetime.TryUpdateDescriptorSets(
                (uint)bindings.Length,
                writes,
                out reason))
        {
            return false;
        }

        reason = "Ready";
        return true;
    }

    private unsafe bool TryUpdateDescriptorRanges(
        DescriptorSet descriptorSet,
        uint textureBase,
        uint textureCount,
        uint samplerBase,
        uint samplerCount,
        out string reason)
    {
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
        uint writeCount = 0u;
        fixed (DescriptorImageInfo* imagePointer = _imageDescriptorScratch)
        fixed (DescriptorImageInfo* samplerPointer = _samplerDescriptorScratch)
        {
            if (textureCount != 0u)
            {
                writes[writeCount++] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSet,
                    DstBinding =
                        AdvancedGlobalResourceBindings.TextureDescriptors,
                    DstArrayElement = textureBase,
                    DescriptorCount = textureCount,
                    DescriptorType = DescriptorType.SampledImage,
                    PImageInfo = imagePointer,
                };
            }
            if (samplerCount != 0u)
            {
                writes[writeCount++] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSet,
                    DstBinding =
                        AdvancedGlobalResourceBindings.SamplerDescriptors,
                    DstArrayElement = samplerBase,
                    DescriptorCount = samplerCount,
                    DescriptorType = DescriptorType.Sampler,
                    PImageInfo = samplerPointer,
                };
            }

            if (writeCount != 0u &&
                !_resources.DescriptorLifetime.TryUpdateDescriptorSets(
                    writeCount,
                    writes,
                    out reason))
            {
                return false;
            }
        }

        reason = "Ready";
        return true;
    }

    private bool TryArmUse(
        VulkanAdvancedSceneResourceSlot slot,
        int frameSlot,
        int entryIndex,
        out VulkanAdvancedScenePublicationUse use,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        use = default;
        if (slot.ReceiptCount >= slot.ReceiptStates.Length)
        {
            failure = EVulkanAdvancedSceneResourceFailure.ReceiptCapacity;
            reason = $"Frame slot {frameSlot} exhausted its {slot.ReceiptStates.Length} native publication receipts.";
            return false;
        }

        ref VulkanAdvancedScenePublicationEntry entry =
            ref slot.Entries[entryIndex];
        VulkanAdvancedScenePublicationUseState receipt =
            slot.ReceiptStates[slot.ReceiptCount++];
        ++entry.ActiveUseCount;
        ++slot.ActiveUseCount;
        use = receipt.Arm(this, frameSlot, entryIndex, entry.State);
        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = "Ready";
        return true;
    }

    private void RetireNativeStorageNoLock()
    {
        if (_descriptorPool.Handle != 0)
        {
            _resources.DescriptorLifetime.RetireDescriptorPool(
                _descriptorPool);
            _descriptorPool = default;
        }
        if (_resourceDescriptorSetLayout.Handle != 0 && _device is { } device)
        {
            _resources.DestroyDescriptorSetLayout(
                device.Api,
                device.Device,
                _resources.FramebufferRetirementFrameSlot,
                _resourceDescriptorSetLayout,
                "AdvancedScene.ResourceDescriptorSetLayout");
            _resourceDescriptorSetLayout = default;
        }
        if (_globalDescriptorSetLayout.Handle != 0 && _device is { } globalDevice)
        {
            _resources.DestroyDescriptorSetLayout(
                globalDevice.Api,
                globalDevice.Device,
                _resources.FramebufferRetirementFrameSlot,
                _globalDescriptorSetLayout,
                "AdvancedScene.GlobalDescriptorSetLayout");
            _globalDescriptorSetLayout = default;
        }
        for (int index = 0; index < _samplerCacheCount; ++index)
        {
            _resources.Samplers.Retire(
                _samplerCache[index],
                "AdvancedScene.SamplerCache");
            _samplerCache[index] = default;
            _samplerCacheRecords[index] = default;
        }
        _samplerCacheCount = 0;
        for (int index = 0; index < _slots.Length; ++index)
        {
            _slots[index].ResourceDescriptorSet = default;
            _slots[index].GlobalDescriptorSets.AsSpan().Clear();
        }
        _device = null;
    }

    private void AllocateScratch(uint capacity)
    {
        int descriptorCount = checked((int)capacity);
        _textureRecordScratch =
            new AdvancedTextureRecord[descriptorCount];
        _encodedTextureScratch =
            new AdvancedEncodedTextureReference[descriptorCount];
        _encodedSamplerScratch =
            new AdvancedEncodedSamplerReference[descriptorCount];
        _imageDescriptorScratch =
            new DescriptorImageInfo[descriptorCount];
        _samplerDescriptorScratch =
            new DescriptorImageInfo[descriptorCount];
        _samplerValidationScratch = new byte[descriptorCount];
    }

    private ulong CalculatePublicationStorageBytes(
        AdvancedGpuScenePublicationSnapshot snapshot)
    {
        AdvancedMaterialPublicationSnapshot material =
            snapshot.MaterialPayloads;
        ulong bytes = 0u;
        AddTableBytes<AdvancedMaterialRecord>(
            material.Materials.PhysicalRecords.Length,
            ref bytes);
        AddTableBytes<AdvancedShadingKernelRecord>(
            material.Kernels.PhysicalRecords.Length,
            ref bytes);
        AddTableBytes<AdvancedMaterialLayoutRecord>(
            material.Layouts.PhysicalRecords.Length,
            ref bytes);
        AddTableBytes<uint>(material.ConstantWords.Length, ref bytes);
        AddTableBytes<AdvancedMaterialTextureBinding>(
            material.TextureBindings.Length,
            ref bytes);
        AddTableBytes<AdvancedTextureRecord>(
            snapshot.Textures.PhysicalRecords.Length,
            ref bytes);
        AddTableBytes<AdvancedSamplerRecord>(
            snapshot.Samplers.PhysicalRecords.Length,
            ref bytes);
        AddTableBytes<AdvancedEncodedTextureReference>(
            snapshot.Textures.PhysicalRecords.Length + 1,
            ref bytes);
        AddTableBytes<AdvancedEncodedSamplerReference>(
            snapshot.Samplers.PhysicalRecords.Length + 1,
            ref bytes);
        int lookupCount = checked(
            material.Materials.HandleLookups.Length +
            material.Kernels.HandleLookups.Length +
            material.Layouts.HandleLookups.Length +
            snapshot.Textures.HandleLookups.Length +
            snapshot.Samplers.HandleLookups.Length);
        AddTableBytes<AdvancedGpuHandleLookup>(lookupCount, ref bytes);
        bytes = checked(
            bytes + AlignUp(FallbackTableByteLength, StorageAlignment));
        return bytes;
    }

    private static void AddTableBytes<T>(int count, ref ulong total)
        where T : unmanaged
    {
        int rowCount = Math.Max(count, 1);
        ulong bytes = checked((ulong)rowCount * (uint)Unsafe.SizeOf<T>());
        total = checked(total + AlignUp(bytes, StorageAlignment));
    }

    private static uint ResolveDescriptorCapacity(
        in PhysicalDeviceProperties properties)
    {
        uint capacity = RequestedDescriptorCapacity;
        uint[] limits =
        [
            properties.Limits.MaxDescriptorSetSampledImages,
            properties.Limits.MaxPerStageDescriptorSampledImages,
            properties.Limits.MaxDescriptorSetSamplers,
            properties.Limits.MaxPerStageDescriptorSamplers,
        ];
        for (int index = 0; index < limits.Length; ++index)
            if (limits[index] != 0u)
                capacity = Math.Min(capacity, limits[index]);
        return capacity;
    }

    private bool SetUnavailable(
        EVulkanAdvancedSceneResourceFailure failure,
        string reason,
        out string outputReason)
    {
        IsReady = false;
        AvailabilityFailure = failure;
        AvailabilityReason = reason;
        outputReason = reason;
        return false;
    }

    private ulong NextNativeGeneration()
    {
        _nextNativeGeneration = _nextNativeGeneration == ulong.MaxValue
            ? 1u
            : _nextNativeGeneration + 1u;
        return _nextNativeGeneration;
    }

    private static AdvancedEncodedTextureReference
        CreateFallbackTextureReference()
        => new(
            0u,
            0u,
            (uint)EAdvancedResourceFallback.Zero,
            EAdvancedResourceReferenceFlags.Fallback);

    private static AdvancedEncodedSamplerReference
        CreateFallbackSamplerReference()
        => new(
            0u,
            0u,
            0u,
            EAdvancedResourceReferenceFlags.Fallback);

    private static bool TextureStateEquals(
        in AdvancedTextureRecord left,
        in AdvancedTextureRecord right)
        => left.Dimension == right.Dimension &&
           left.Flags == right.Flags && left.Width == right.Width &&
           left.Height == right.Height &&
           left.DepthOrLayers == right.DepthOrLayers &&
           left.MipCount == right.MipCount &&
           left.FormatClass == right.FormatClass &&
           left.UvScaleBias == right.UvScaleBias;

    private static string DescribeTextureStateMismatch(
        in AdvancedTextureRecord current,
        in AdvancedTextureRecord published)
        => $"dimension={current.Dimension}/{published.Dimension}, flags={current.Flags}/{published.Flags}, extent={current.Width}x{current.Height}x{current.DepthOrLayers}/{published.Width}x{published.Height}x{published.DepthOrLayers}, mips={current.MipCount}/{published.MipCount}, format={current.FormatClass}/{published.FormatClass}, uv={current.UvScaleBias}/{published.UvScaleBias}";

    private static bool SamplerStateEquals(
        in AdvancedSamplerRecord left,
        in AdvancedSamplerRecord right)
        => left.Filter == right.Filter && left.Flags == right.Flags &&
           left.AddressU == right.AddressU &&
           left.AddressV == right.AddressV &&
           left.AddressW == right.AddressW &&
           left.CompareOperation == right.CompareOperation &&
           VectorBitsEqual(
               left.LodBiasMinMaxAnisotropy,
               right.LodBiasMinMaxAnisotropy) &&
           VectorBitsEqual(left.BorderColor, right.BorderColor);

    private static string DescribeSamplerStateMismatch(
        in AdvancedSamplerRecord current,
        in AdvancedSamplerRecord published)
        => $"filter={current.Filter}/{published.Filter}, flags={current.Flags}/{published.Flags}, address={current.AddressU},{current.AddressV},{current.AddressW}/{published.AddressU},{published.AddressV},{published.AddressW}, compare={current.CompareOperation}/{published.CompareOperation}, lod={current.LodBiasMinMaxAnisotropy}/{published.LodBiasMinMaxAnisotropy}, border={current.BorderColor}/{published.BorderColor}";

    private static bool VectorBitsEqual(Vector4 left, Vector4 right)
        => CanonicalFloatBits(left.X) == CanonicalFloatBits(right.X) &&
           CanonicalFloatBits(left.Y) == CanonicalFloatBits(right.Y) &&
           CanonicalFloatBits(left.Z) == CanonicalFloatBits(right.Z) &&
           CanonicalFloatBits(left.W) == CanonicalFloatBits(right.W);

    private static uint CanonicalFloatBits(float value)
        => value == 0.0f ? 0u : BitConverter.SingleToUInt32Bits(value);

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsKnownFilter(EAdvancedSamplerFilter filter)
        => filter is EAdvancedSamplerFilter.Nearest or
            EAdvancedSamplerFilter.Linear or
            EAdvancedSamplerFilter.Anisotropic;

    private static bool TryMapAddress(
        EAdvancedSamplerAddressMode source,
        out SamplerAddressMode destination)
    {
        destination = source switch
        {
            EAdvancedSamplerAddressMode.Repeat =>
                SamplerAddressMode.Repeat,
            EAdvancedSamplerAddressMode.MirroredRepeat =>
                SamplerAddressMode.MirroredRepeat,
            EAdvancedSamplerAddressMode.ClampToEdge =>
                SamplerAddressMode.ClampToEdge,
            EAdvancedSamplerAddressMode.ClampToBorder =>
                SamplerAddressMode.ClampToBorder,
            _ => default,
        };
        return source is EAdvancedSamplerAddressMode.Repeat or
            EAdvancedSamplerAddressMode.MirroredRepeat or
            EAdvancedSamplerAddressMode.ClampToEdge or
            EAdvancedSamplerAddressMode.ClampToBorder;
    }

    private static bool TryMapCompare(
        EAdvancedCompareOperation source,
        out CompareOp destination)
    {
        destination = source switch
        {
            EAdvancedCompareOperation.Never => CompareOp.Never,
            EAdvancedCompareOperation.Less => CompareOp.Less,
            EAdvancedCompareOperation.Equal => CompareOp.Equal,
            EAdvancedCompareOperation.LessOrEqual =>
                CompareOp.LessOrEqual,
            EAdvancedCompareOperation.Greater => CompareOp.Greater,
            EAdvancedCompareOperation.NotEqual => CompareOp.NotEqual,
            EAdvancedCompareOperation.GreaterOrEqual =>
                CompareOp.GreaterOrEqual,
            EAdvancedCompareOperation.Always => CompareOp.Always,
            _ => default,
        };
        return source is >= EAdvancedCompareOperation.Never and
            <= EAdvancedCompareOperation.Always;
    }

    private static ulong AlignUp(ulong value, ulong alignment)
        => alignment <= 1u
            ? value
            : checked((value + alignment - 1u) / alignment * alignment);
}
