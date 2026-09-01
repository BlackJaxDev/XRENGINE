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
    // The sealed scene table image is rebuilt after target invalidation (for
    // example, a desktop resize). Keep this boundary-owned lane large enough
    // for the observed full canonical publication without permitting native
    // arena growth from the frame-preparation hot path.
    private const ulong StorageCapacityPerFrameSlot = 32ul * 1024ul * 1024ul;
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

    /// <summary>Owning resource generation for adjacent advanced ABI services.</summary>
    internal VulkanResourceRuntime Resources => _resources;

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
            if (!TryValidateStorageReservationBudget(arena, out string storageBudgetReason))
            {
                return SetUnavailable(
                    EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity,
                    storageBudgetReason,
                    out reason);
            }
            if (!arena.TryReserveLaneCapacity(
                    EVulkanFrameDataLane.AdvancedSceneStorage,
                    StorageCapacityPerFrameSlot,
                    StorageAlignment))
            {
                return SetUnavailable(
                    EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity,
                    $"Failed to reserve the declared {StorageCapacityPerFrameSlot}-byte advanced-scene storage budget per frame slot " +
                    $"({GetStorageReservationBytes()} bytes across {_slots.Length} slots). The frame-data arena enforces a " +
                    $"{VulkanFrameDataArena.MaximumMappedBytes}-byte aggregate mapped-memory guard.",
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
                    "[VulkanAdvancedScene] Descriptor-indexing resource runtime ready: frameSlots={0}, descriptorCapacity={1}, globalSet={2}, resourceSet={3}, storageBytesPerSlot={4}, storageReservationBytes={5}, frameArenaAllocatedBytes={6}.",
                    _slots.Length,
                    DescriptorCapacity,
                    GlobalSetIndex,
                    ResourceSetIndex,
                    StorageCapacityPerFrameSlot,
                    GetStorageReservationBytes(),
                    arena.AllocatedBytes);
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
        ReadOnlySpan<BackendReadyCanonicalViewRecord> views,
        in BackendReadyCanonicalFrameRecord frame,
        ReadOnlySpan<BackendReadyCanonicalPassRecord> passes,
        ReadOnlySpan<AdvancedGlobalPassPublicationCoverage> globalPassCoverage,
        int diagnosticCount,
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
                failure = slot.TransactionIntegrityFault
                    ? EVulkanAdvancedSceneResourceFailure.TransactionIntegrityFailure
                    : EVulkanAdvancedSceneResourceFailure.NativeFault;
                reason = slot.TransactionIntegrityFault
                    ? "The advanced-scene frame slot is quarantined because its frame-data transaction could not be rolled back."
                    : "The advanced-scene descriptor set for this frame slot is quarantined after a native publication fault.";
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
            if (snapshot.Submission.Sequence != publication.Sequence ||
                snapshot.Mutations.Sequence != publication.Sequence ||
                snapshot.ReverseDependencies.Sequence != publication.Sequence ||
                !snapshot.ReverseDependencies.IsComplete ||
                snapshot.GlobalPassCoverage.Sequence != publication.Sequence)
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.DependencyManifestInconsistent;
                reason = "The retained canonical publication has an incomplete or sequence-mismatched submission, mutation, reverse-dependency, or global-pass manifest.";
                return false;
            }
            if (!TryValidateGlobalPassCoverage(
                    snapshot,
                    passes,
                    globalPassCoverage,
                    publication.Sequence,
                    out reason))
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.DependencyManifestInconsistent;
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
                    views,
                    in frame,
                    passes,
                    diagnosticCount,
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

    /// <summary>
    /// Revalidates the mutable texture sources retained by a canonical package
    /// before its visibility family can enter an accepted frame. Streaming may
    /// replace an <see cref="XRTexture"/> image after the package captured its
    /// logical record; that package must be retried on a later publication.
    /// </summary>
    internal bool TryValidatePackageSourcesForAdmission(
        BackendReadyFramePackage package,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(package);
        lock (_gate)
        {
            if (!package.TryGetCanonicalPublicationSnapshot(
                    out AdvancedGpuScenePublicationSnapshot snapshot) ||
                snapshot.Submission.Sequence !=
                    package.CanonicalScenePublication.Sequence)
            {
                failure =
                    EVulkanAdvancedSceneResourceFailure.PublicationSnapshotUnavailable;
                reason =
                    "The canonical package no longer retains its exact scene publication.";
                return false;
            }

            return TryValidatePublicationSources(
                snapshot,
                out failure,
                out reason);
        }
    }

    private static bool TryValidateGlobalPassCoverage(
        AdvancedGpuScenePublicationSnapshot snapshot,
        ReadOnlySpan<BackendReadyCanonicalPassRecord> passes,
        ReadOnlySpan<AdvancedGlobalPassPublicationCoverage> coverage,
        ulong publicationSequence,
        out string reason)
    {
        if (coverage.Length != passes.Length)
        {
            reason =
                $"Canonical global-pass coverage count {coverage.Length} does not match pass count {passes.Length}.";
            return false;
        }

        AdvancedGlobalPassPublicationCoverage source =
            snapshot.GlobalPassCoverage;
        ReadOnlySpan<AdvancedDrawSubmissionRecord> submissions =
            snapshot.Submission.Records;
        for (int passIndex = 0; passIndex < passes.Length; ++passIndex)
        {
            BackendReadyCanonicalPassRecord pass = passes[passIndex];
            AdvancedGlobalPassPublicationCoverage candidate =
                coverage[passIndex];
            bool usesShadows = false;
            bool usesProbes = false;
            for (int submissionIndex = 0;
                 submissionIndex < submissions.Length;
                 ++submissionIndex)
            {
                AdvancedDrawSubmissionRecord submission =
                    submissions[submissionIndex];
                if (submission.PassIndex != unchecked((uint)pass.PassIndex))
                    continue;

                GPUIndirectRenderFlags flags =
                    (GPUIndirectRenderFlags)submission.Flags;
                usesShadows |= (flags &
                    (GPUIndirectRenderFlags.CastShadow |
                     GPUIndirectRenderFlags.ReceiveShadows)) != 0;
                usesProbes |=
                    (flags & GPUIndirectRenderFlags.Unlit) == 0;
                if (usesShadows && usesProbes)
                    break;
            }

            if (candidate.PassIndex != pass.PassIndex ||
                candidate.Sequence != publicationSequence ||
                candidate.ShadowGenerations != source.ShadowGenerations ||
                candidate.ProbeGenerations != source.ProbeGenerations ||
                candidate.ShadowDirtyRange != source.ShadowDirtyRange ||
                candidate.ProbeDirtyRange != source.ProbeDirtyRange ||
                candidate.UsesShadows != usesShadows ||
                candidate.UsesProbes != usesProbes)
            {
                reason =
                    $"Canonical global-pass coverage at index {passIndex} does not match pass {pass.PassIndex} in publication {publicationSequence}.";
                return false;
            }
        }

        reason = "Ready";
        return true;
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

    /// <summary>
    /// Validates the fixed scene-lane declaration before native allocation.
    /// The arena repeats this check while reserving, but reporting the exact
    /// requested reservation here makes an invalid startup budget actionable.
    /// </summary>
    private bool TryValidateStorageReservationBudget(
        VulkanFrameDataArena arena,
        out string reason)
    {
        ulong reservationBytes = GetStorageReservationBytes();
        ulong allocatedBytes = unchecked((ulong)Math.Max(arena.AllocatedBytes, 0L));
        if (reservationBytes > VulkanFrameDataArena.MaximumMappedBytes ||
            allocatedBytes > VulkanFrameDataArena.MaximumMappedBytes - reservationBytes)
        {
            reason =
                $"The declared advanced-scene reservation of {StorageCapacityPerFrameSlot} bytes per frame slot " +
                $"({reservationBytes} bytes across {_slots.Length} slots) cannot fit within the " +
                $"{VulkanFrameDataArena.MaximumMappedBytes}-byte frame-data arena aggregate guard after {allocatedBytes} bytes are already allocated.";
            return false;
        }

        reason = "Ready";
        return true;
    }

    private ulong GetStorageReservationBytes()
        => checked(StorageCapacityPerFrameSlot * (ulong)_slots.Length);

    private static string DescribeStorageCapacityExhaustion(
        ulong requiredBytes,
        ulong consumedBytes,
        ulong compactRequiredBytes)
        => $"Frame-slot advanced-scene storage requires {requiredBytes} bytes after {consumedBytes} of the declared " +
           $"{StorageCapacityPerFrameSlot}-byte budget were consumed (compact rebuild requires {compactRequiredBytes} bytes). " +
           "This boundary-owned lane is fixed for the renderer generation and cannot grow from frame preparation; the failure is not retryable.";

    private bool TryBuildPublication(
        VulkanAdvancedSceneResourceSlot slot,
        int frameSlot,
        ulong frameGeneration,
        AdvancedGpuScenePublicationSnapshot snapshot,
        ReadOnlySpan<BackendReadyCanonicalViewRecord> views,
        in BackendReadyCanonicalFrameRecord frame,
        ReadOnlySpan<BackendReadyCanonicalPassRecord> passes,
        int diagnosticCount,
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

        if (diagnosticCount < 0 || frame.FrameGeneration == 0u)
        {
            failure = EVulkanAdvancedSceneResourceFailure.InvalidPublication;
            reason = "The canonical frame metadata is invalid for Vulkan publication.";
            return false;
        }

        VulkanAdvancedScenePublicationAllocationPlan allocationPlan =
            BuildPublicationAllocationPlan(
                slot,
                snapshot,
                textureHighWater,
                samplerHighWater,
                views.Length,
                passes.Length);
        ulong requiredStorage = allocationPlan.RequiredBytes;
        VulkanFrameDataArena storageArena = _resources.FrameDataArena!;
        if (!storageArena.TryCaptureReservedLaneCursor(
                frameSlot,
                EVulkanFrameDataLane.AdvancedSceneStorage,
                out ulong rollbackCursor))
        {
            failure = EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            reason = "The advanced-scene allocation cursor is unavailable.";
            return false;
        }

        // Preflight both legal completed-slot layouts before retaining any
        // native slice. A retained prefix can preserve obsolete capacity and
        // falsely reject a current snapshot which fits when packed from zero.
        ulong retainedCursor = allocationPlan.GetRetainedEnd(rollbackCursor);
        ulong retainedConsumedStorage = AlignUp(
            Math.Max(slot.StorageBytesConsumed, retainedCursor),
            StorageAlignment);
        bool retainFits = requiredStorage <= StorageCapacityPerFrameSlot &&
            retainedConsumedStorage <= StorageCapacityPerFrameSlot - requiredStorage;
        ulong compactRequiredStorage = allocationPlan.CompactRequiredBytes;
        ulong compactConsumedStorage = AlignUp(
            Math.Max(slot.StorageBytesConsumed, rollbackCursor),
            StorageAlignment);
        bool compactFits = compactRequiredStorage <= StorageCapacityPerFrameSlot &&
            compactConsumedStorage <= StorageCapacityPerFrameSlot - compactRequiredStorage;
        if (!retainFits && compactFits)
        {
            allocationPlan.SelectCompactRebuild();
            requiredStorage = allocationPlan.RequiredBytes;
        }
        else if (!retainFits)
        {
            failure = EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            reason = DescribeStorageCapacityExhaustion(
                requiredStorage,
                retainedConsumedStorage,
                compactRequiredStorage);
            return false;
        }
        if (!TryRetainPlannedResidentSlices(slot, allocationPlan) ||
            !storageArena.TryCaptureReservedLaneCursor(
                frameSlot,
                EVulkanFrameDataLane.AdvancedSceneStorage,
                out ulong allocationCursor))
        {
            if (!TryRollbackStorageTransaction(
                    slot, storageArena, frameSlot, rollbackCursor, out string rollbackReason))
            {
                failure = EVulkanAdvancedSceneResourceFailure.TransactionIntegrityFailure;
                reason = rollbackReason;
                return false;
            }
            failure = EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            reason = "The completed-slot resident ranges could not be retained before advanced-scene allocation.";
            return false;
        }
        // The sealed plan charges every allocation at its aligned size. Charge
        // the initial cursor pad as well: retained resident slices can end at
        // an unaligned byte, while the first subsequent arena allocation will
        // align before consuming its payload.
        ulong consumedStorage = AlignUp(
            Math.Max(slot.StorageBytesConsumed, allocationCursor),
            StorageAlignment);
        if (requiredStorage > StorageCapacityPerFrameSlot ||
            consumedStorage > StorageCapacityPerFrameSlot - requiredStorage)
        {
            if (!TryRollbackStorageTransaction(
                    slot, storageArena, frameSlot, rollbackCursor, out string rollbackReason))
            {
                failure = EVulkanAdvancedSceneResourceFailure.TransactionIntegrityFailure;
                reason = rollbackReason;
                return false;
            }
            failure =
                EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            reason = DescribeStorageCapacityExhaustion(
                requiredStorage,
                consumedStorage,
                allocationPlan.CompactRequiredBytes);
            return false;
        }
        ulong plannedEndCursor = checked(consumedStorage + requiredStorage);

        if (!TryUploadPublicationTables(
                frameSlot,
                slot,
                allocationPlan,
                snapshot,
                textureHighWater,
                samplerHighWater,
                 out VulkanFrameDataSlice drawSlice,
                 out VulkanFrameDataSlice instanceSlice,
                 out VulkanFrameDataSlice geometrySlice,
                 out VulkanFrameDataSlice staticVertexSlice,
                 out VulkanFrameDataSlice indexSlice,
                 out VulkanFrameDataSlice preSkinnedCurrentSlice,
                 out VulkanFrameDataSlice preSkinnedPreviousSlice,
                 out VulkanFrameDataSlice meshletDescriptorSlice,
                 out VulkanFrameDataSlice meshletVertexIndexSlice,
                 out VulkanFrameDataSlice meshletTriangleWordSlice,
                 out VulkanFrameDataSlice transformSlice,
                 out VulkanFrameDataSlice deformationSlice,
                 out VulkanFrameDataSlice renderStateSlice,
                 out VulkanFrameDataSlice editorIdentitySlice,
                 out VulkanFrameDataSlice materialSlice,
                out VulkanFrameDataSlice kernelSlice,
                out VulkanFrameDataSlice layoutSlice,
                out VulkanFrameDataSlice constantSlice,
                out VulkanFrameDataSlice bindingSlice,
                out VulkanFrameDataSlice textureSlice,
                out VulkanFrameDataSlice samplerSlice,
                out VulkanFrameDataSlice lightSlice,
                out VulkanFrameDataSlice shadowSlice,
                out VulkanFrameDataSlice probeSlice,
                out VulkanFrameDataSlice environmentSlice,
                out VulkanFrameDataSlice decalSlice,
                out VulkanFrameDataSlice giResourceSlice,
                views,
                in frame,
                passes,
                diagnosticCount,
                out VulkanFrameDataSlice viewSlice,
                out VulkanFrameDataSlice frameMetadataSlice,
                out VulkanFrameDataSlice encodedTextureSlice,
                out VulkanFrameDataSlice encodedSamplerSlice,
                out VulkanFrameDataSlice lookupSlice,
                out VulkanFrameDataSlice fallbackTableSlice,
                out VulkanAdvancedSceneLookupSegments lookupSegments))
        {
            if (!TryRollbackStorageTransaction(
                    slot, storageArena, frameSlot, rollbackCursor, out string rollbackReason))
            {
                failure = EVulkanAdvancedSceneResourceFailure.TransactionIntegrityFailure;
                reason = rollbackReason;
                return false;
            }
            failure =
                EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            reason = "The boundary-reserved advanced-scene storage lane could not publish the complete immutable table image.";
            return false;
        }

        if (!storageArena.TryCaptureReservedLaneCursor(
                frameSlot,
                EVulkanFrameDataLane.AdvancedSceneStorage,
                out ulong publishedCursor) ||
            AlignUp(publishedCursor, StorageAlignment) != plannedEndCursor)
        {
            if (!TryRollbackStorageTransaction(
                    slot, storageArena, frameSlot, rollbackCursor, out string rollbackReason))
            {
                failure = EVulkanAdvancedSceneResourceFailure.TransactionIntegrityFailure;
                reason = rollbackReason;
                return false;
            }
            failure = EVulkanAdvancedSceneResourceFailure.TransactionIntegrityFailure;
            reason = $"The sealed advanced-scene allocation plan ended at {plannedEndCursor} bytes, but the mapped arena ended at {publishedCursor} bytes.";
            return false;
        }

        DescriptorSet globalDescriptorSet =
            slot.GlobalDescriptorSets[slot.EntryCount];
        if (!TryUpdateGlobalTableDescriptors(
                 globalDescriptorSet,
                 fallbackTableSlice,
                 drawSlice,
                 instanceSlice,
                 geometrySlice,
                 transformSlice,
                 deformationSlice,
                 renderStateSlice,
                 editorIdentitySlice,
                 materialSlice,
                kernelSlice,
                layoutSlice,
                constantSlice,
                bindingSlice,
                textureSlice,
                samplerSlice,
                lightSlice,
                shadowSlice,
                probeSlice,
                environmentSlice,
                decalSlice,
                giResourceSlice,
                viewSlice,
                frameMetadataSlice,
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
            if (!TryRollbackStorageTransaction(
                    slot, storageArena, frameSlot, rollbackCursor, out string rollbackReason))
            {
                failure = EVulkanAdvancedSceneResourceFailure.TransactionIntegrityFailure;
                reason = rollbackReason;
                return false;
            }
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
             drawSlice,
             instanceSlice,
             geometrySlice,
             staticVertexSlice,
             indexSlice,
             preSkinnedCurrentSlice,
             preSkinnedPreviousSlice,
             meshletDescriptorSlice,
             meshletVertexIndexSlice,
             meshletTriangleWordSlice,
             transformSlice,
             deformationSlice,
             renderStateSlice,
             editorIdentitySlice,
             materialSlice,
            kernelSlice,
            layoutSlice,
            constantSlice,
            bindingSlice,
            textureSlice,
            samplerSlice,
            lightSlice,
            shadowSlice,
            probeSlice,
            environmentSlice,
            decalSlice,
            giResourceSlice,
            viewSlice,
            frameMetadataSlice,
            encodedTextureSlice,
            encodedSamplerSlice,
            lookupSlice,
            fallbackTableSlice,
            lookupSegments);
        // The sealed preflight is charged only once the complete publication,
        // including both descriptor updates, is visible. Failed retries cannot
        // leak logical capacity even though the arena itself remains monotonic.
        slot.StorageBytesConsumed = plannedEndCursor;
        failure = EVulkanAdvancedSceneResourceFailure.None;
        reason = "Ready";
        return true;
    }

    /// <summary>
    /// Restores the transaction cursor before discarding resident mirrors. A
    /// failed restore means the arena can no longer prove which tail ranges
    /// are owned, so this slot must never publish another generation.
    /// </summary>
    private static bool TryRollbackStorageTransaction(
        VulkanAdvancedSceneResourceSlot slot,
        VulkanFrameDataArena arena,
        int frameSlot,
        ulong rollbackCursor,
        out string reason)
    {
        if (arena.TryRestoreReservedLaneCursor(
                frameSlot,
                EVulkanFrameDataLane.AdvancedSceneStorage,
                rollbackCursor))
        {
            slot.ClearResidentMirrors();
            reason = string.Empty;
            return true;
        }

        slot.Quarantined = true;
        slot.TransactionIntegrityFault = true;
        reason = "The advanced-scene frame-data transaction could not restore its reserved-lane cursor; the frame slot was quarantined.";
        return false;
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
        VulkanAdvancedSceneResourceSlot slot,
        VulkanAdvancedScenePublicationAllocationPlan allocationPlan,
        AdvancedGpuScenePublicationSnapshot snapshot,
        int textureHighWater,
        int samplerHighWater,
        out VulkanFrameDataSlice draws,
        out VulkanFrameDataSlice instances,
        out VulkanFrameDataSlice geometry,
        out VulkanFrameDataSlice staticVertices,
        out VulkanFrameDataSlice indices,
        out VulkanFrameDataSlice preSkinnedCurrent,
        out VulkanFrameDataSlice preSkinnedPrevious,
        out VulkanFrameDataSlice meshletDescriptors,
        out VulkanFrameDataSlice meshletVertexIndices,
        out VulkanFrameDataSlice meshletTriangleWords,
        out VulkanFrameDataSlice transforms,
        out VulkanFrameDataSlice deformations,
        out VulkanFrameDataSlice renderStates,
        out VulkanFrameDataSlice editorIdentities,
        out VulkanFrameDataSlice materials,
        out VulkanFrameDataSlice kernels,
        out VulkanFrameDataSlice layouts,
        out VulkanFrameDataSlice constants,
        out VulkanFrameDataSlice bindings,
        out VulkanFrameDataSlice textures,
        out VulkanFrameDataSlice samplers,
        out VulkanFrameDataSlice lights,
        out VulkanFrameDataSlice shadows,
        out VulkanFrameDataSlice probes,
        out VulkanFrameDataSlice environments,
        out VulkanFrameDataSlice decals,
        out VulkanFrameDataSlice giResources,
        ReadOnlySpan<BackendReadyCanonicalViewRecord> sourceViews,
        in BackendReadyCanonicalFrameRecord frame,
        ReadOnlySpan<BackendReadyCanonicalPassRecord> passes,
        int diagnosticCount,
        out VulkanFrameDataSlice views,
        out VulkanFrameDataSlice frameMetadata,
        out VulkanFrameDataSlice encodedTextures,
        out VulkanFrameDataSlice encodedSamplers,
        out VulkanFrameDataSlice lookups,
        out VulkanFrameDataSlice fallbackTable,
        out VulkanAdvancedSceneLookupSegments lookupSegments)
    {
        draws = default;
        instances = default;
        geometry = default;
        staticVertices = default;
        indices = default;
        preSkinnedCurrent = default;
        preSkinnedPrevious = default;
        meshletDescriptors = default;
        meshletVertexIndices = default;
        meshletTriangleWords = default;
        transforms = default;
        deformations = default;
        renderStates = default;
        editorIdentities = default;
        materials = default;
        kernels = default;
        layouts = default;
        constants = default;
        bindings = default;
        textures = default;
        samplers = default;
        lights = default;
        shadows = default;
        probes = default;
        environments = default;
        decals = default;
        giResources = default;
        views = default;
        frameMetadata = default;
        encodedTextures = default;
        encodedSamplers = default;
        lookups = default;
        fallbackTable = default;
        lookupSegments = default;
        AdvancedMaterialPublicationSnapshot material =
            snapshot.MaterialPayloads;
        return TryUploadResident(
                   slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Draws,
                   frameSlot, snapshot.Draws.PhysicalRecords,
                   snapshot.Draws, snapshot.DatabaseEpoch, slot.ResidentDraws, out draws) &&
               TryUploadResident(
                   slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Instances,
                   frameSlot, snapshot.Instances.PhysicalRecords,
                   snapshot.Instances, snapshot.DatabaseEpoch, slot.ResidentInstances, out instances) &&
               TryUploadResident(
                   slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Geometry,
                   frameSlot, snapshot.Geometry.PhysicalRecords,
                   snapshot.Geometry, snapshot.DatabaseEpoch, slot.ResidentGeometry, out geometry) &&
               TryUploadResidentBytes(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.StaticVertices, frameSlot, snapshot.GeometryPayloads.StaticVertices, snapshot.DatabaseEpoch, slot.ResidentStaticVertices, out staticVertices) &&
               TryUploadResidentBytes(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Indices, frameSlot, snapshot.GeometryPayloads.Indices, snapshot.DatabaseEpoch, slot.ResidentIndices, out indices) &&
               TryUploadResidentBytes(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.PreSkinnedCurrent, frameSlot, snapshot.GeometryPayloads.PreSkinnedCurrent, snapshot.DatabaseEpoch, slot.ResidentPreSkinnedCurrent, out preSkinnedCurrent) &&
               TryUploadResidentBytes(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.PreSkinnedPrevious, frameSlot, snapshot.GeometryPayloads.PreSkinnedPrevious, snapshot.DatabaseEpoch, slot.ResidentPreSkinnedPrevious, out preSkinnedPrevious) &&
               TryUploadResidentBytes(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.MeshletDescriptors, frameSlot, snapshot.GeometryPayloads.MeshletDescriptors, snapshot.DatabaseEpoch, slot.ResidentMeshletDescriptors, out meshletDescriptors) &&
               TryUploadResidentBytes(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.MeshletVertexIndices, frameSlot, snapshot.GeometryPayloads.MeshletVertexIndices, snapshot.DatabaseEpoch, slot.ResidentMeshletVertexIndices, out meshletVertexIndices) &&
               TryUploadResidentBytes(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.MeshletTriangleWords, frameSlot, snapshot.GeometryPayloads.MeshletTriangleWords, snapshot.DatabaseEpoch, slot.ResidentMeshletTriangleWords, out meshletTriangleWords) &&
               TryUploadResident(
                   slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Transforms,
                   frameSlot, snapshot.Transforms.PhysicalRecords,
                   snapshot.Transforms, snapshot.DatabaseEpoch, slot.ResidentTransforms, out transforms) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Deformations, frameSlot,
                   snapshot.Deformations.PhysicalRecords,
                   snapshot.Deformations, snapshot.DatabaseEpoch, slot.ResidentDeformations,
                   out deformations) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.RenderStates, frameSlot,
                   snapshot.RenderStates.PhysicalRecords,
                   snapshot.RenderStates, snapshot.DatabaseEpoch, slot.ResidentRenderStates,
                   out renderStates) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.EditorIdentities, frameSlot,
                   snapshot.EditorIdentities.PhysicalRecords,
                   snapshot.EditorIdentities, snapshot.DatabaseEpoch, slot.ResidentEditorIdentities,
                   out editorIdentities) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Materials, frameSlot,
                   material.Materials.PhysicalRecords,
                   material.Materials, snapshot.DatabaseEpoch, slot.ResidentMaterials,
                   out materials) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Kernels, frameSlot, material.Kernels.PhysicalRecords,
                   material.Kernels, snapshot.DatabaseEpoch, slot.ResidentKernels, out kernels) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Layouts, frameSlot, material.Layouts.PhysicalRecords,
                   material.Layouts, snapshot.DatabaseEpoch, slot.ResidentLayouts, out layouts) &&
               TryUploadResidentBytes(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.MaterialConstants, frameSlot, MemoryMarshal.AsBytes(material.ConstantWords), material.Generations.MaterialRows.Content, snapshot.DatabaseEpoch, slot.ResidentMaterialConstants, out constants) &&
               TryUploadResidentBytes(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.MaterialBindings, frameSlot, MemoryMarshal.AsBytes(material.TextureBindings), material.Generations.MaterialRows.Content, snapshot.DatabaseEpoch, slot.ResidentMaterialBindings, out bindings) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Textures, frameSlot,
                   _textureRecordScratch.AsSpan(0, textureHighWater),
                   snapshot.Textures, snapshot.DatabaseEpoch, slot.ResidentTextures, out textures) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Samplers, frameSlot, snapshot.Samplers.PhysicalRecords,
                   snapshot.Samplers, snapshot.DatabaseEpoch, slot.ResidentSamplers, out samplers) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Lights, frameSlot, snapshot.GlobalResources.Lights.PhysicalRecords,
                   snapshot.GlobalResources.Lights, snapshot.DatabaseEpoch, slot.ResidentLights, out lights) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Shadows, frameSlot, snapshot.GlobalResources.Shadows.PhysicalRecords,
                   snapshot.GlobalResources.Shadows, snapshot.DatabaseEpoch, slot.ResidentShadows, out shadows) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Probes, frameSlot, snapshot.GlobalResources.Probes.PhysicalRecords,
                   snapshot.GlobalResources.Probes, snapshot.DatabaseEpoch, slot.ResidentProbes, out probes) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Environments, frameSlot, snapshot.GlobalResources.Environments.PhysicalRecords,
                   snapshot.GlobalResources.Environments, snapshot.DatabaseEpoch, slot.ResidentEnvironments, out environments) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.Decals, frameSlot, snapshot.GlobalResources.Decals.PhysicalRecords,
                   snapshot.GlobalResources.Decals, snapshot.DatabaseEpoch, slot.ResidentDecals, out decals) &&
               TryUploadResident(slot, allocationPlan, EVulkanAdvancedSceneResidentOwner.GiResources, frameSlot, snapshot.GlobalResources.GiResources.PhysicalRecords,
                   snapshot.GlobalResources.GiResources, snapshot.DatabaseEpoch, slot.ResidentGiResources, out giResources) &&
               TryUploadLookups(
                   frameSlot,
                   slot,
                   allocationPlan,
                   snapshot,
                   material,
                   out lookups,
                   out lookupSegments) &&
               TryUploadFallbackTable(frameSlot, out fallbackTable) &&
               TryUploadViews(frameSlot, sourceViews, out views) &&
               TryUploadFrameMetadata(
                   frameSlot,
                   in frame,
                   sourceViews.Length,
                   passes,
                   diagnosticCount,
                   out frameMetadata) &&
               TryUpload(
                   frameSlot,
                   _encodedTextureScratch.AsSpan(0, textureHighWater + 1),
                   out encodedTextures) &&
               TryUpload(
                   frameSlot,
                   _encodedSamplerScratch.AsSpan(0, samplerHighWater + 1),
                   out encodedSamplers);
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

    private bool TryRetainPlannedResidentSlices(
        VulkanAdvancedSceneResourceSlot slot,
        VulkanAdvancedScenePublicationAllocationPlan plan)
        => TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Draws, slot.ResidentDraws.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Instances, slot.ResidentInstances.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Geometry, slot.ResidentGeometry.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.StaticVertices, slot.ResidentStaticVertices.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Indices, slot.ResidentIndices.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.PreSkinnedCurrent, slot.ResidentPreSkinnedCurrent.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.PreSkinnedPrevious, slot.ResidentPreSkinnedPrevious.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.MeshletDescriptors, slot.ResidentMeshletDescriptors.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.MeshletVertexIndices, slot.ResidentMeshletVertexIndices.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.MeshletTriangleWords, slot.ResidentMeshletTriangleWords.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Transforms, slot.ResidentTransforms.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Deformations, slot.ResidentDeformations.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.RenderStates, slot.ResidentRenderStates.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.EditorIdentities, slot.ResidentEditorIdentities.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Materials, slot.ResidentMaterials.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Kernels, slot.ResidentKernels.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Layouts, slot.ResidentLayouts.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.MaterialConstants, slot.ResidentMaterialConstants.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.MaterialBindings, slot.ResidentMaterialBindings.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Textures, slot.ResidentTextures.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Samplers, slot.ResidentSamplers.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Lights, slot.ResidentLights.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Shadows, slot.ResidentShadows.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Probes, slot.ResidentProbes.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Environments, slot.ResidentEnvironments.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Decals, slot.ResidentDecals.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.GiResources, slot.ResidentGiResources.Slice) &&
           TryRetainPlannedResidentSlice(plan,
               EVulkanAdvancedSceneResidentOwner.Lookups, slot.ResidentLookups.Slice);

    private bool TryRetainPlannedResidentSlice(
        VulkanAdvancedScenePublicationAllocationPlan plan,
        EVulkanAdvancedSceneResidentOwner owner,
        in VulkanFrameDataSlice slice)
        => !plan.IsPatch(owner) ||
           _resources.FrameDataArena!.TryRetainResidentSlice(slice);

    /// <summary>
    /// Publishes a core SoA table into a completed-slot resident range when
    /// doing so cannot mutate an earlier immutable entry.  Later publications
    /// in the same generation always receive a COW image.  The publication
    /// deltas select the exact normal write set. Remaps update both dense-row
    /// endpoints, while truncated tails are cleared explicitly.
    /// </summary>
    private bool TryUploadResident<T>(
        VulkanAdvancedSceneResourceSlot slot,
        VulkanAdvancedScenePublicationAllocationPlan allocationPlan,
        EVulkanAdvancedSceneResidentOwner owner,
        int frameSlot,
        ReadOnlySpan<T> source,
        AdvancedGpuRecordTablePublicationSnapshot<T> table,
        ulong databaseEpoch,
        VulkanAdvancedSceneResidentTable<T> resident,
        out VulkanFrameDataSlice slice)
        where T : unmanaged
    {
        ReadOnlySpan<AdvancedGpuRecordPublicationDelta> deltas = table.Deltas;
        slice = default;
        if (!allocationPlan.IsPatch(owner))
        {
            if (!TryUpload(frameSlot, source, out slice))
                return false;

            if (slot.EntryCount == 0 && slot.ActiveUseCount == 0)
            {
                if (!resident.TryInitialize(slice, source))
                    return false;
                resident.StampPublication(databaseEpoch, table.Sequence, table.Generations);
            }

            return true;
        }

        if (resident.CanReuseUnchanged(databaseEpoch, table.Generations, source))
        {
            resident.StampPublication(databaseEpoch, table.Sequence, table.Generations);
            slice = resident.CreateCurrentSlice(source.Length);
            return true;
        }

        bool deltaPatch = resident.CanPatch(
            databaseEpoch,
            table.Sequence,
            table.JournalFloorSequence,
            table.HasRetainedJournal,
            source);

        if (!deltaPatch && !TryWriteResidentBytes(
                resident.Slice,
                0,
                MemoryMarshal.AsBytes(source)))
            return false;

        for (int index = 0; deltaPatch && index < deltas.Length; ++index)
        {
            AdvancedGpuRecordPublicationDelta delta = deltas[index];

            if (delta.PublicationGeneration <= resident.AppliedPublicationSequence)
                continue;

            if (!TryPatchResidentRow(resident.Slice, source, delta.CurrentDenseIndex))
                return false;

            if (delta.Change == EAdvancedGpuRecordPublicationChange.DenseRemapped &&
                !TryPatchResidentRow(resident.Slice, source, delta.PreviousDenseIndex))
                return false;
        }
        int previousCount = resident.Count;
        if (source.Length < previousCount && !TryClearResidentRange<T>(
                resident.Slice, source.Length, previousCount - source.Length))
            return false;
        if (deltaPatch)
            resident.CommitPatched(source, deltas);
        else
            resident.Commit(source);
        resident.StampPublication(databaseEpoch, table.Sequence, table.Generations);
        slice = resident.CreateCurrentSlice(source.Length);
        return true;
    }

    private bool TryUploadResidentBytes(
        VulkanAdvancedSceneResourceSlot slot,
        VulkanAdvancedScenePublicationAllocationPlan allocationPlan,
        EVulkanAdvancedSceneResidentOwner owner,
        int frameSlot,
        in AdvancedImmutableByteArenaPublicationSnapshot snapshot,
        ulong databaseEpoch,
        VulkanAdvancedSceneResidentBytes resident,
        out VulkanFrameDataSlice slice)
        => TryUploadResidentBytes(
            slot,
            allocationPlan,
            owner,
            frameSlot,
            snapshot.Data,
            snapshot.DirtyByteRange,
            snapshot.BufferHandle,
            databaseEpoch,
            0u,
            resident,
            out slice);

    private bool TryUploadResidentBytes(
        VulkanAdvancedSceneResourceSlot slot,
        VulkanAdvancedScenePublicationAllocationPlan allocationPlan,
        EVulkanAdvancedSceneResidentOwner owner,
        int frameSlot,
        ReadOnlySpan<byte> source,
        ulong ownerGeneration,
        ulong databaseEpoch,
        VulkanAdvancedSceneResidentBytes resident,
        out VulkanFrameDataSlice slice)
        => TryUploadResidentBytes(
            slot,
            allocationPlan,
            owner,
            frameSlot,
            source,
            new AdvancedGpuDirtyRange(0u, checked((uint)source.Length)),
            AdvancedGpuHandle.Invalid,
            databaseEpoch,
            ownerGeneration,
            resident,
            out slice);

    private bool TryUploadResidentBytes(
        VulkanAdvancedSceneResourceSlot slot,
        VulkanAdvancedScenePublicationAllocationPlan allocationPlan,
        EVulkanAdvancedSceneResidentOwner owner,
        int frameSlot,
        ReadOnlySpan<byte> source,
        AdvancedGpuDirtyRange dirtyRange,
        AdvancedGpuHandle bufferHandle,
        ulong databaseEpoch,
        ulong ownerGeneration,
        VulkanAdvancedSceneResidentBytes resident,
        out VulkanFrameDataSlice slice)
    {
        slice = default;
        if (!allocationPlan.IsPatch(owner))
        {
            if (!TryUpload(frameSlot, source, out slice))
                return false;

            if (slot.EntryCount != 0 || slot.ActiveUseCount != 0)
                return true;

            if (!resident.TryInitialize(slice, source))
                return false;

            resident.SetBufferHandle(bufferHandle);
            resident.SetDatabaseEpoch(databaseEpoch);
            resident.SetPublishedOwnerGeneration(ownerGeneration);
            return true;
        }
        bool sameEpoch = databaseEpoch != 0u &&
            resident.DatabaseEpoch == databaseEpoch;
        bool appendPatch = bufferHandle.IsValid && sameEpoch &&
            resident.BufferHandle == bufferHandle && source.Length >= resident.Count;
        bool unchanged = !bufferHandle.IsValid && sameEpoch &&
            ownerGeneration == resident.PublishedOwnerGeneration;
        int start = appendPatch
            ? resident.Count
            : unchanged
                ? source.Length
                : 0;
        int endExclusive = appendPatch || !unchanged
            ? source.Length
            : checked((int)Math.Min(dirtyRange.EndExclusive, (uint)source.Length));
        if (start < endExclusive && !TryWriteResidentBytes(
                resident.Slice, start, source.Slice(start, endExclusive - start)))
            return false;

        int priorCount = resident.Count;
        if (source.Length < priorCount && !TryClearResidentBytes(
                resident.Slice, source.Length, priorCount - source.Length))
            return false;
        int previousCount = resident.Count;
        uint committedStart = appendPatch
            ? checked((uint)previousCount)
            : unchanged ? dirtyRange.Start : 0u;
        uint committedCount = appendPatch
            ? checked((uint)Math.Max(source.Length - previousCount, 0))
            : unchanged
                ? 0u
                : checked((uint)source.Length);
        resident.CommitPatched(source, new AdvancedGpuDirtyRange(
            committedStart,
            committedCount));
        resident.SetPublishedOwnerGeneration(ownerGeneration);
        resident.SetBufferHandle(bufferHandle);
        resident.SetDatabaseEpoch(databaseEpoch);
        slice = resident.CurrentSlice(source.Length);
        return true;
    }

    private bool TryPatchResidentRow<T>(
        in VulkanFrameDataSlice residentSlice,
        ReadOnlySpan<T> source,
        uint denseIndex)
        where T : unmanaged
    {
        if (denseIndex == AdvancedGpuHandleRemap.InvalidDenseIndex ||
            denseIndex >= residentSlice.Length / (uint)Unsafe.SizeOf<T>())
        {
            return true;
        }
        VulkanFrameDataSlice row = CreateWriteSubSlice(
            residentSlice,
            checked((ulong)denseIndex * (uint)Unsafe.SizeOf<T>()),
            (uint)Unsafe.SizeOf<T>());
        if (!_resources.FrameDataArena!.TryBeginWrite(row, out VulkanFrameDataWriteScope write))
            return false;
        using (write)
            MemoryMarshal.Cast<byte, T>(write.Bytes)[0] = denseIndex < (uint)source.Length
                ? source[checked((int)denseIndex)]
                : default;
        return true;
    }

    private bool TryClearResidentRange<T>(
        in VulkanFrameDataSlice residentSlice,
        int start,
        int count)
        where T : unmanaged
        => TryClearResidentBytes(
            residentSlice,
            checked(start * Unsafe.SizeOf<T>()),
            checked(count * Unsafe.SizeOf<T>()));

    private bool TryWriteResidentBytes(
        in VulkanFrameDataSlice residentSlice,
        int offset,
        ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
            return true;
        VulkanFrameDataSlice writeSlice = CreateWriteSubSlice(
            residentSlice, checked((ulong)offset), checked((uint)source.Length));
        if (!_resources.FrameDataArena!.TryBeginWrite(writeSlice, out VulkanFrameDataWriteScope write))
            return false;
        using (write)
            source.CopyTo(write.Bytes);
        return true;
    }

    private bool TryClearResidentBytes(
        in VulkanFrameDataSlice residentSlice,
        int offset,
        int length)
    {
        if (length <= 0)
            return true;
        VulkanFrameDataSlice clearSlice = CreateWriteSubSlice(
            residentSlice, checked((ulong)offset), checked((uint)length));
        if (!_resources.FrameDataArena!.TryBeginWrite(clearSlice, out VulkanFrameDataWriteScope write))
            return false;
        using (write)
            write.Bytes.Clear();
        return true;
    }

    /// <summary>
    /// Narrows mapping/flush bookkeeping to an exact resident byte interval.
    /// Host writes need no storage-buffer alignment; using byte alignment here
    /// preserves the parent slice's ownership while accepting dense row sizes.
    /// </summary>
    private static VulkanFrameDataSlice CreateWriteSubSlice(
        in VulkanFrameDataSlice residentSlice,
        ulong relativeOffset,
        uint length)
    {
        if (length == 0u || relativeOffset > residentSlice.Length ||
            length > residentSlice.Length - relativeOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        return residentSlice with
        {
            Offset = checked(residentSlice.Offset + relativeOffset),
            Length = length,
            Alignment = 1u,
        };
    }

    private bool TryUploadViews(
        int frameSlot,
        ReadOnlySpan<BackendReadyCanonicalViewRecord> source,
        out VulkanFrameDataSlice slice)
    {
        // Stereo and multiview frames fit this fixed ABI budget. Rejecting a
        // wider view family is preferable to allocating in the hot path.
        if (source.Length > 8)
        {
            slice = default;
            return false;
        }
        int count = Math.Max(source.Length, 1);
        Span<AdvancedViewRecord> records = stackalloc AdvancedViewRecord[8];
        for (int index = 0; index < source.Length; ++index)
            records[index] = VulkanAdvancedViewRecordFactory.Create(in source[index]);
        return _resources.FrameDataArena!.TryAllocateWrite(
            frameSlot,
            EVulkanFrameDataLane.AdvancedSceneStorage,
            MemoryMarshal.AsBytes(records[..count]),
            StorageAlignment,
            out slice);
    }

    private bool TryUploadFrameMetadata(
        int frameSlot,
        in BackendReadyCanonicalFrameRecord frame,
        int viewCount,
        ReadOnlySpan<BackendReadyCanonicalPassRecord> passes,
        int diagnosticCount,
        out VulkanFrameDataSlice slice)
    {
        int byteLength = checked(
            Unsafe.SizeOf<VulkanAdvancedFrameMetadataHeader>() +
            passes.Length * Unsafe.SizeOf<VulkanAdvancedPassRecord>());
        if (!_resources.FrameDataArena!.TryAllocate(
                frameSlot,
                EVulkanFrameDataLane.AdvancedSceneStorage,
                (uint)Math.Max(byteLength, 1),
                StorageAlignment,
                out slice) ||
            !_resources.FrameDataArena.TryBeginWrite(
                slice,
                out VulkanFrameDataWriteScope write))
        {
            slice = default;
            return false;
        }

        using (write)
        {
            write.Bytes.Clear();
            ref VulkanAdvancedFrameMetadataHeader header = ref
                MemoryMarshal.AsRef<VulkanAdvancedFrameMetadataHeader>(write.Bytes);
            header = new VulkanAdvancedFrameMetadataHeader
            {
                FrameId = frame.FrameId,
                FrameGeneration = frame.FrameGeneration,
                SourceRevision = frame.SourceRevision,
                DependencySignature = frame.DependencySignature,
                ViewCount = checked((uint)viewCount),
                PassCount = checked((uint)passes.Length),
                DiagnosticCount = checked((uint)diagnosticCount),
            };
            Span<VulkanAdvancedPassRecord> destination = MemoryMarshal.Cast<
                byte,
                VulkanAdvancedPassRecord>(
                write.Bytes[Unsafe.SizeOf<VulkanAdvancedFrameMetadataHeader>()..]);
            for (int index = 0; index < passes.Length; ++index)
                destination[index] = VulkanAdvancedPassRecord.FromCanonical(in passes[index]);
        }
        return true;
    }

    private bool TryUploadLookups(
        int frameSlot,
        VulkanAdvancedSceneResourceSlot slot,
        VulkanAdvancedScenePublicationAllocationPlan allocationPlan,
        AdvancedGpuScenePublicationSnapshot snapshot,
        AdvancedMaterialPublicationSnapshot material,
        out VulkanFrameDataSlice slice,
        out VulkanAdvancedSceneLookupSegments segments)
    {
        Span<uint> counts = stackalloc uint[VulkanAdvancedSceneResidentLookups.OwnerCount];
        FillLookupCounts(counts, snapshot, material);
        bool boundary = slot.EntryCount == 0 && slot.ActiveUseCount == 0;
        VulkanAdvancedSceneResidentLookups resident = slot.ResidentLookups;
        if (allocationPlan.IsPatch(EVulkanAdvancedSceneResidentOwner.Lookups))
        {
            bool exactPatch = resident.CanPatch(snapshot.DatabaseEpoch, counts);
            bool anyChanged = !exactPatch;
            for (int owner = 0; owner < VulkanAdvancedSceneResidentLookups.OwnerCount; ++owner)
            {
                if (exactPatch && !resident.IsOwnerUnchanged(owner,
                        GetLookupGeneration(owner, snapshot, material)))
                {
                    anyChanged = true;
                    break;
                }
            }
            if (!anyChanged)
            {
                for (int owner = 0; owner < VulkanAdvancedSceneResidentLookups.OwnerCount; ++owner)
                    resident.StampOwner(owner, counts[owner],
                        GetLookupGeneration(owner, snapshot, material),
                        GetLookupSequence(owner, snapshot, material));
                resident.SetDatabaseEpoch(snapshot.DatabaseEpoch);
                slice = resident.Slice;
                segments = CreateLookupSegments(resident, counts);
                return true;
            }

            for (int owner = 0; owner < VulkanAdvancedSceneResidentLookups.OwnerCount; ++owner)
            {
                ReadOnlySpan<AdvancedGpuHandleLookup> source = GetLookupSource(owner, snapshot, material);
                ulong lookupGeneration = GetLookupGeneration(owner, snapshot, material);
                if (!exactPatch || !resident.IsOwnerUnchanged(owner, lookupGeneration))
                {
                    int elementSize = Unsafe.SizeOf<AdvancedGpuHandleLookup>();
                    int offset = checked((int)resident.GetOffset(owner) * elementSize);
                    if (!TryWriteResidentBytes(resident.Slice, offset,
                            MemoryMarshal.AsBytes(source)) ||
                        !TryClearResidentBytes(resident.Slice,
                            checked(offset + source.Length * elementSize),
                            checked(((int)resident.GetCapacity(owner) - source.Length) * elementSize)))
                    {
                        slice = default;
                        segments = default;
                        return false;
                    }
                }
                resident.StampOwner(owner, (uint)source.Length, lookupGeneration,
                    GetLookupSequence(owner, snapshot, material));
            }
            resident.SetDatabaseEpoch(snapshot.DatabaseEpoch);
            slice = resident.Slice;
            segments = CreateLookupSegments(resident, counts);
            return true;
        }

        Span<uint> capacities = stackalloc uint[VulkanAdvancedSceneResidentLookups.OwnerCount];
        for (int owner = 0; owner < capacities.Length; ++owner)
            capacities[owner] = resident.GetRequiredCapacity(
                owner,
                counts[owner],
                boundary && !allocationPlan.IsCompactRebuild);
        uint total = 0u;
        for (int owner = 0; owner < capacities.Length; ++owner)
            total = checked(total + capacities[owner]);

        VulkanFrameDataArena arena = _resources.FrameDataArena!;
        if (!arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedSceneStorage,
                checked(total * (uint)Unsafe.SizeOf<AdvancedGpuHandleLookup>()), StorageAlignment,
                out slice) || !arena.TryBeginWrite(slice, out VulkanFrameDataWriteScope write))
        {
            slice = default;
            segments = default;
            return false;
        }
        Span<ulong> generations = stackalloc ulong[VulkanAdvancedSceneResidentLookups.OwnerCount];
        Span<ulong> sequences = stackalloc ulong[VulkanAdvancedSceneResidentLookups.OwnerCount];
        using (write)
        {
            Span<AdvancedGpuHandleLookup> destination = MemoryMarshal.Cast<byte, AdvancedGpuHandleLookup>(write.Bytes);
            destination.Fill(AdvancedGpuHandleLookup.Invalid);
            uint offset = 0u;
            for (int owner = 0; owner < VulkanAdvancedSceneResidentLookups.OwnerCount; ++owner)
            {
                ReadOnlySpan<AdvancedGpuHandleLookup> source = GetLookupSource(owner, snapshot, material);
                source.CopyTo(destination.Slice(checked((int)offset), source.Length));
                generations[owner] = GetLookupGeneration(owner, snapshot, material);
                sequences[owner] = GetLookupSequence(owner, snapshot, material);
                offset = checked(offset + capacities[owner]);
            }
        }
        if (boundary)
            resident.Initialize(slice, snapshot.DatabaseEpoch, capacities, counts, generations, sequences);
        segments = CreateLookupSegments(capacities, counts);
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
            ShaderStageFlags.ComputeBit |
            (device.SupportsMeshTaskIndirectCount
                ? ShaderStageFlags.MeshBitExt
                : 0);
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
        in VulkanFrameDataSlice draws,
        in VulkanFrameDataSlice instances,
        in VulkanFrameDataSlice geometry,
        in VulkanFrameDataSlice transforms,
        in VulkanFrameDataSlice deformations,
        in VulkanFrameDataSlice renderStates,
        in VulkanFrameDataSlice editorIdentities,
        in VulkanFrameDataSlice materials,
        in VulkanFrameDataSlice kernels,
        in VulkanFrameDataSlice layouts,
        in VulkanFrameDataSlice constants,
        in VulkanFrameDataSlice materialTextureBindings,
        in VulkanFrameDataSlice textures,
        in VulkanFrameDataSlice samplers,
        in VulkanFrameDataSlice lights,
        in VulkanFrameDataSlice shadows,
        in VulkanFrameDataSlice probes,
        in VulkanFrameDataSlice environments,
        in VulkanFrameDataSlice decals,
        in VulkanFrameDataSlice giResources,
        in VulkanFrameDataSlice views,
        in VulkanFrameDataSlice frameMetadata,
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
                AdvancedGlobalResourceBindings.Draws => draws,
                AdvancedGlobalResourceBindings.Instances => instances,
                AdvancedGlobalResourceBindings.Meshes => geometry,
                AdvancedGlobalResourceBindings.Transforms => transforms,
                AdvancedGlobalResourceBindings.Deformations => deformations,
                AdvancedGlobalResourceBindings.RenderStates => renderStates,
                AdvancedGlobalResourceBindings.EditorIdentities =>
                    editorIdentities,
                AdvancedGlobalResourceBindings.Materials => materials,
                AdvancedGlobalResourceBindings.Textures => textures,
                AdvancedGlobalResourceBindings.Samplers => samplers,
                AdvancedGlobalResourceBindings.Lights => lights,
                AdvancedGlobalResourceBindings.Shadows => shadows,
                AdvancedGlobalResourceBindings.Probes => probes,
                AdvancedGlobalResourceBindings.Environments => environments,
                AdvancedGlobalResourceBindings.Decals => decals,
                AdvancedGlobalResourceBindings.GiResources => giResources,
                AdvancedGlobalResourceBindings.Views => views,
                AdvancedGlobalResourceBindings.Diagnostics => frameMetadata,
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
            _slots[index].ClearResidentMirrors();
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

    private VulkanAdvancedScenePublicationAllocationPlan
        BuildPublicationAllocationPlan(
            VulkanAdvancedSceneResourceSlot slot,
            AdvancedGpuScenePublicationSnapshot snapshot,
            int textureHighWater,
            int samplerHighWater,
            int viewCount,
            int passCount)
    {
        AdvancedMaterialPublicationSnapshot material =
            snapshot.MaterialPayloads;
        VulkanAdvancedScenePublicationAllocationPlan plan = slot.AllocationPlan;
        plan.Reset();
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Draws,
            slot, snapshot.DatabaseEpoch, snapshot.Draws, slot.ResidentDraws);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Instances,
            slot, snapshot.DatabaseEpoch, snapshot.Instances, slot.ResidentInstances);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Geometry,
            slot, snapshot.DatabaseEpoch, snapshot.Geometry, slot.ResidentGeometry);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.StaticVertices,
            slot, snapshot.DatabaseEpoch, snapshot.GeometryPayloads.StaticVertices, slot.ResidentStaticVertices);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Indices,
            slot, snapshot.DatabaseEpoch, snapshot.GeometryPayloads.Indices, slot.ResidentIndices);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.PreSkinnedCurrent,
            slot, snapshot.DatabaseEpoch, snapshot.GeometryPayloads.PreSkinnedCurrent, slot.ResidentPreSkinnedCurrent);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.PreSkinnedPrevious,
            slot, snapshot.DatabaseEpoch, snapshot.GeometryPayloads.PreSkinnedPrevious, slot.ResidentPreSkinnedPrevious);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.MeshletDescriptors,
            slot, snapshot.DatabaseEpoch, snapshot.GeometryPayloads.MeshletDescriptors, slot.ResidentMeshletDescriptors);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.MeshletVertexIndices,
            slot, snapshot.DatabaseEpoch, snapshot.GeometryPayloads.MeshletVertexIndices, slot.ResidentMeshletVertexIndices);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.MeshletTriangleWords,
            slot, snapshot.DatabaseEpoch, snapshot.GeometryPayloads.MeshletTriangleWords, slot.ResidentMeshletTriangleWords);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Transforms,
            slot, snapshot.DatabaseEpoch, snapshot.Transforms, slot.ResidentTransforms);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Deformations,
            slot, snapshot.DatabaseEpoch, snapshot.Deformations, slot.ResidentDeformations);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.RenderStates,
            slot, snapshot.DatabaseEpoch, snapshot.RenderStates, slot.ResidentRenderStates);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.EditorIdentities,
            slot, snapshot.DatabaseEpoch, snapshot.EditorIdentities, slot.ResidentEditorIdentities);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Materials,
            slot, snapshot.DatabaseEpoch, material.Materials, slot.ResidentMaterials);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Kernels,
            slot, snapshot.DatabaseEpoch, material.Kernels, slot.ResidentKernels);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Layouts,
            slot, snapshot.DatabaseEpoch, material.Layouts, slot.ResidentLayouts);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.MaterialConstants,
            slot, MemoryMarshal.AsBytes(material.ConstantWords), slot.ResidentMaterialConstants);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.MaterialBindings,
            slot, MemoryMarshal.AsBytes(material.TextureBindings), slot.ResidentMaterialBindings);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Textures,
            slot, snapshot.DatabaseEpoch, snapshot.Textures, slot.ResidentTextures);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Samplers,
            slot, snapshot.DatabaseEpoch, snapshot.Samplers, slot.ResidentSamplers);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Lights,
            slot, snapshot.DatabaseEpoch, snapshot.GlobalResources.Lights, slot.ResidentLights);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Shadows,
            slot, snapshot.DatabaseEpoch, snapshot.GlobalResources.Shadows, slot.ResidentShadows);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Probes,
            slot, snapshot.DatabaseEpoch, snapshot.GlobalResources.Probes, slot.ResidentProbes);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Environments,
            slot, snapshot.DatabaseEpoch, snapshot.GlobalResources.Environments, slot.ResidentEnvironments);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.Decals,
            slot, snapshot.DatabaseEpoch, snapshot.GlobalResources.Decals, slot.ResidentDecals);
        SetResidentDecision(plan, EVulkanAdvancedSceneResidentOwner.GiResources,
            slot, snapshot.DatabaseEpoch, snapshot.GlobalResources.GiResources, slot.ResidentGiResources);
        SetLookupDecision(plan, slot, snapshot, material);

        // Direct resident slices are embedded in immutable publication entries.
        // At a completed boundary we may either retain the entire prior packed
        // image or transactionally rebuild the entire image from cursor zero;
        // mixing retained and COW owners would strand unreachable arena gaps.
        plan.SealResidentPacking();

        plan.AddTransient(GetTableBytes<AdvancedEncodedTextureReference>(
            textureHighWater + 1));
        plan.AddTransient(GetTableBytes<AdvancedEncodedSamplerReference>(
            samplerHighWater + 1));
        plan.AddTransient(AlignUp(FallbackTableByteLength, StorageAlignment));
        plan.AddTransient(CalculateFramePublicationStorageBytes(
            viewCount,
            passCount));
        return plan;
    }

    private void SetResidentDecision<T>(
        VulkanAdvancedScenePublicationAllocationPlan plan,
        EVulkanAdvancedSceneResidentOwner owner,
        VulkanAdvancedSceneResourceSlot slot,
        ulong databaseEpoch,
        AdvancedGpuRecordTablePublicationSnapshot<T> table,
        VulkanAdvancedSceneResidentTable<T> resident)
        where T : unmanaged
    {
        ReadOnlySpan<T> source = table.PhysicalRecords;
        bool patch = slot.EntryCount == 0 &&
            slot.ActiveUseCount == 0 && resident.MatchesCapacity(source) &&
            _resources.FrameDataArena!.CanRetainResidentSlice(resident.Slice);
        ulong bytes = GetTableBytes<T>(source.Length);
        plan.SetPatch(owner, patch, bytes, bytes, GetResidentEnd(resident.Slice));
    }

    private void SetLookupDecision(
        VulkanAdvancedScenePublicationAllocationPlan plan,
        VulkanAdvancedSceneResourceSlot slot,
        AdvancedGpuScenePublicationSnapshot snapshot,
        AdvancedMaterialPublicationSnapshot material)
    {
        Span<uint> counts = stackalloc uint[VulkanAdvancedSceneResidentLookups.OwnerCount];
        FillLookupCounts(counts, snapshot, material);
        bool boundary = slot.EntryCount == 0 && slot.ActiveUseCount == 0;
        VulkanAdvancedSceneResidentLookups resident = slot.ResidentLookups;
        bool patch = boundary && resident.MatchesCapacity(counts) &&
            _resources.FrameDataArena!.CanRetainResidentSlice(resident.Slice);
        uint capacity = resident.GetRequiredCapacity(counts, boundary);
        uint compactCapacity = resident.GetRequiredCapacity(counts, false);
        plan.SetPatch(EVulkanAdvancedSceneResidentOwner.Lookups, patch,
            GetTableBytes<AdvancedGpuHandleLookup>(checked((int)capacity)),
            GetTableBytes<AdvancedGpuHandleLookup>(checked((int)compactCapacity)),
            GetResidentEnd(resident.Slice));
    }

    private static void FillLookupCounts(
        Span<uint> destination,
        AdvancedGpuScenePublicationSnapshot snapshot,
        AdvancedMaterialPublicationSnapshot material)
    {
        for (int owner = 0; owner < VulkanAdvancedSceneResidentLookups.OwnerCount; ++owner)
            destination[owner] = checked((uint)GetLookupSource(owner, snapshot, material).Length);
    }

    private static ReadOnlySpan<AdvancedGpuHandleLookup> GetLookupSource(
        int owner,
        AdvancedGpuScenePublicationSnapshot snapshot,
        AdvancedMaterialPublicationSnapshot material)
        => owner switch
        {
            0 => snapshot.Draws.HandleLookups,
            1 => snapshot.Instances.HandleLookups,
            2 => snapshot.Geometry.HandleLookups,
            3 => snapshot.Transforms.HandleLookups,
            4 => snapshot.Deformations.HandleLookups,
            5 => snapshot.RenderStates.HandleLookups,
            6 => snapshot.EditorIdentities.HandleLookups,
            7 => material.Materials.HandleLookups,
            8 => material.Kernels.HandleLookups,
            9 => material.Layouts.HandleLookups,
            10 => snapshot.Textures.HandleLookups,
            11 => snapshot.Samplers.HandleLookups,
            _ => throw new ArgumentOutOfRangeException(nameof(owner)),
        };

    private static ulong GetLookupGeneration(
        int owner,
        AdvancedGpuScenePublicationSnapshot snapshot,
        AdvancedMaterialPublicationSnapshot material)
        => owner switch
        {
            0 => snapshot.Draws.Generations.Lookup,
            1 => snapshot.Instances.Generations.Lookup,
            2 => snapshot.Geometry.Generations.Lookup,
            3 => snapshot.Transforms.Generations.Lookup,
            4 => snapshot.Deformations.Generations.Lookup,
            5 => snapshot.RenderStates.Generations.Lookup,
            6 => snapshot.EditorIdentities.Generations.Lookup,
            7 => material.Materials.Generations.Lookup,
            8 => material.Kernels.Generations.Lookup,
            9 => material.Layouts.Generations.Lookup,
            10 => snapshot.Textures.Generations.Lookup,
            11 => snapshot.Samplers.Generations.Lookup,
            _ => throw new ArgumentOutOfRangeException(nameof(owner)),
        };

    private static ulong GetLookupSequence(
        int owner,
        AdvancedGpuScenePublicationSnapshot snapshot,
        AdvancedMaterialPublicationSnapshot material)
        => owner switch
        {
            0 => snapshot.Draws.Sequence,
            1 => snapshot.Instances.Sequence,
            2 => snapshot.Geometry.Sequence,
            3 => snapshot.Transforms.Sequence,
            4 => snapshot.Deformations.Sequence,
            5 => snapshot.RenderStates.Sequence,
            6 => snapshot.EditorIdentities.Sequence,
            7 => material.Materials.Sequence,
            8 => material.Kernels.Sequence,
            9 => material.Layouts.Sequence,
            10 => snapshot.Textures.Sequence,
            11 => snapshot.Samplers.Sequence,
            _ => throw new ArgumentOutOfRangeException(nameof(owner)),
        };

    private static VulkanAdvancedSceneLookupSegments CreateLookupSegments(
        VulkanAdvancedSceneResidentLookups resident,
        ReadOnlySpan<uint> counts)
    {
        Span<uint> capacities = stackalloc uint[VulkanAdvancedSceneResidentLookups.OwnerCount];
        for (int owner = 0; owner < capacities.Length; ++owner)
            capacities[owner] = resident.GetCapacity(owner);
        return CreateLookupSegments(capacities, counts);
    }

    private static VulkanAdvancedSceneLookupSegments CreateLookupSegments(
        ReadOnlySpan<uint> capacities,
        ReadOnlySpan<uint> counts)
    {
        Span<uint> offsets = stackalloc uint[VulkanAdvancedSceneResidentLookups.OwnerCount];
        for (int owner = 1; owner < offsets.Length; ++owner)
            offsets[owner] = checked(offsets[owner - 1] + capacities[owner - 1]);
        return new VulkanAdvancedSceneLookupSegments(
            new AdvancedGpuLookupSegment(offsets[0], counts[0]),
            new AdvancedGpuLookupSegment(offsets[1], counts[1]),
            new AdvancedGpuLookupSegment(offsets[2], counts[2]),
            new AdvancedGpuLookupSegment(offsets[3], counts[3]),
            new AdvancedGpuLookupSegment(offsets[4], counts[4]),
            new AdvancedGpuLookupSegment(offsets[5], counts[5]),
            new AdvancedGpuLookupSegment(offsets[6], counts[6]),
            new AdvancedGpuLookupSegment(offsets[7], counts[7]),
            new AdvancedGpuLookupSegment(offsets[8], counts[8]),
            new AdvancedGpuLookupSegment(offsets[9], counts[9]),
            new AdvancedGpuLookupSegment(offsets[10], counts[10]),
            new AdvancedGpuLookupSegment(offsets[11], counts[11]));
    }

    private void SetResidentDecision(
        VulkanAdvancedScenePublicationAllocationPlan plan,
        EVulkanAdvancedSceneResidentOwner owner,
        VulkanAdvancedSceneResourceSlot slot,
        ReadOnlySpan<byte> source,
        VulkanAdvancedSceneResidentBytes resident)
    {
        bool patch = slot.EntryCount == 0 &&
            slot.ActiveUseCount == 0 && resident.MatchesCapacity(source) &&
            _resources.FrameDataArena!.CanRetainResidentSlice(resident.Slice);
        ulong bytes = GetTableBytes<byte>(source.Length);
        plan.SetPatch(owner, patch, bytes, bytes, GetResidentEnd(resident.Slice));
    }

    private void SetResidentDecision(
        VulkanAdvancedScenePublicationAllocationPlan plan,
        EVulkanAdvancedSceneResidentOwner owner,
        VulkanAdvancedSceneResourceSlot slot,
        ulong databaseEpoch,
        in AdvancedImmutableByteArenaPublicationSnapshot snapshot,
        VulkanAdvancedSceneResidentBytes resident)
    {
        ReadOnlySpan<byte> source = snapshot.Data;
        bool patch = slot.EntryCount == 0 &&
            slot.ActiveUseCount == 0 && resident.MatchesCapacity(source) &&
            _resources.FrameDataArena!.CanRetainResidentSlice(resident.Slice);
        ulong bytes = GetTableBytes<byte>(source.Length);
        plan.SetPatch(owner, patch, bytes, bytes, GetResidentEnd(resident.Slice));
    }

    private static ulong CalculateFramePublicationStorageBytes(
        int viewCount,
        int passCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(viewCount);
        ArgumentOutOfRangeException.ThrowIfNegative(passCount);
        if (viewCount > 8)
            throw new InvalidOperationException(
                "The canonical advanced Vulkan ABI supports at most eight views per frame publication.");

        ulong viewBytes = checked((ulong)Math.Max(viewCount, 1) *
            (uint)Unsafe.SizeOf<AdvancedViewRecord>());
        ulong metadataBytes = checked(
            (ulong)Unsafe.SizeOf<VulkanAdvancedFrameMetadataHeader>() +
            (ulong)passCount * (uint)Unsafe.SizeOf<VulkanAdvancedPassRecord>());
        return checked(
            AlignUp(viewBytes, StorageAlignment) +
            AlignUp(metadataBytes, StorageAlignment));
    }

    private static void AddTableBytes<T>(int count, ref ulong total)
        where T : unmanaged
        => total = checked(total + GetTableBytes<T>(count));

    private static ulong GetTableBytes<T>(int count)
        where T : unmanaged
    {
        int rowCount = Math.Max(count, 1);
        ulong bytes = checked((ulong)rowCount * (uint)Unsafe.SizeOf<T>());
        return AlignUp(bytes, StorageAlignment);
    }

    private static ulong GetResidentEnd(in VulkanFrameDataSlice slice)
        => slice.IsValid ? checked(slice.Offset + slice.Length) : 0u;

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
