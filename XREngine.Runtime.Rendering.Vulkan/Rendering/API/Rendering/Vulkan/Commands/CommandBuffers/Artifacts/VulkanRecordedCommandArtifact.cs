using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable owned slot for one native recorded command buffer and every
/// recording-visible identity needed to validate, execute, and retire it.
/// </summary>
internal sealed class VulkanRecordedCommandArtifact(
    CommandBufferLevel level,
    int frameSlot,
    VulkanCommandChainState? mutationAuthority = null)
{
    private CommandRecordingDependencySignature _dependencyIdentity;
    private int _referencedResourceCount;

    internal CommandBuffer NativeBuffer { get; private set; }
    internal CommandBufferLevel Level { get; } = level;
    internal CommandPool OwnerPool { get; private set; }
    internal VulkanWorkerSecondaryCommandArena? WorkerArenaOwner { get; private set; }
    internal ulong ArenaOwnerIdentity =>
        WorkerArenaOwner?.Identity ?? OwnerPool.Handle;
    internal bool OwnsPool { get; private set; }
    internal CommandRecordingDependencySignature DependencyIdentity
    {
        get => _dependencyIdentity;
        private set => _dependencyIdentity = value;
    }
    internal ref readonly CommandRecordingDependencySignature DependencyIdentityReference
        => ref _dependencyIdentity;
    internal int FrameSlot { get; } = frameSlot;
    internal ulong Generation { get; private set; }
    internal ulong RecordingGeneration { get; private set; }
    internal EVulkanRecordedCommandArtifactState State { get; private set; }
    internal EVulkanRecordedCommandArtifactInvalidationReason InvalidationReason { get; private set; }
    internal int QueuedSubmissionCount { get; private set; }
    internal int RecordedPrimaryReferenceCount { get; private set; }
    internal ulong ReferencedResourceIdentity { get; private set; }
    internal int ReferencedResourceCount => _referencedResourceCount;
    internal bool HasInheritance { get; private set; }
    internal VulkanRecordedCommandInheritance Inheritance { get; private set; }
    internal bool IsExecutable =>
        State == EVulkanRecordedCommandArtifactState.Executable &&
        NativeBuffer.Handle != 0;
    internal bool IsPending =>
        QueuedSubmissionCount != 0 ||
        RecordedPrimaryReferenceCount != 0 ||
        State == EVulkanRecordedCommandArtifactState.PendingRetirement;

    internal bool TryValidateCommandChainSecondaryDependency(
        in CommandRecordingDependencySignature expected,
        out CommandRecordingDependencyMismatch mismatch)
    {
        mismatch = _dependencyIdentity.CompareCommandChainSecondary(in expected);
        return IsExecutable && !mismatch.RequiresRecording;
    }

    internal VulkanRecordedCommandArtifactReference CreateReference()
    {
        VulkanCommandIdentityComponents dependencyComponents =
            _dependencyIdentity.CaptureIdentityComponents();
        FrameOpSignatureHasher resources = new();
        resources.Add(dependencyComponents.ResourceGenerations);
        resources.Add(unchecked((ulong)NativeBuffer.Handle));
        resources.Add(RecordingGeneration);
        resources.Add(ReferencedResourceIdentity);

        return new VulkanRecordedCommandArtifactReference(
            NativeBuffer,
            Level,
            FrameSlot,
            Generation,
            RecordingGeneration,
            ReferencedResourceIdentity,
            IsExecutable,
            dependencyComponents with
            {
                ResourceGenerations = resources.ToHash(),
                RenderScopeInheritance = HasInheritance
                    ? Inheritance.ComputeIdentity()
                    : dependencyComponents.RenderScopeInheritance,
                PrimaryOnly = 0,
            });
    }

    internal void AssignNativeBuffer(
        CommandBuffer commandBuffer,
        CommandPool ownerPool,
        bool ownsPool,
        VulkanWorkerSecondaryCommandArena? workerArenaOwner = null)
    {
        if (!ReferenceEquals(WorkerArenaOwner, workerArenaOwner))
            WorkerArenaOwner?.Detach(this);

        NativeBuffer = commandBuffer;
        OwnerPool = ownerPool;
        OwnsPool = ownsPool;
        WorkerArenaOwner = workerArenaOwner;
        WorkerArenaOwner?.Attach(this);
        RecordingGeneration = 0;
        DependencyIdentity = default;
        QueuedSubmissionCount = 0;
        RecordedPrimaryReferenceCount = 0;
        ClearPublishedRecording();
        State = commandBuffer.Handle == 0
            ? EVulkanRecordedCommandArtifactState.Empty
            : EVulkanRecordedCommandArtifactState.Allocated;
        InvalidationReason = EVulkanRecordedCommandArtifactInvalidationReason.None;
        AdvanceGeneration();
    }

    internal void BeginRecording(ulong recordingGeneration)
    {
        if (NativeBuffer.Handle == 0)
            throw new InvalidOperationException("A recorded command artifact requires a native buffer before recording.");
        if (IsPending)
            throw new InvalidOperationException("A pending recorded command artifact cannot begin a new recording.");

        RecordingGeneration = recordingGeneration;
        ClearPublishedRecording();
        State = EVulkanRecordedCommandArtifactState.Recording;
        InvalidationReason =
            EVulkanRecordedCommandArtifactInvalidationReason.RecordingStarted;
        AdvanceGeneration();
    }

    internal void StoreInheritance(in VulkanRecordedCommandInheritance inheritance)
    {
        Inheritance = inheritance;
        HasInheritance = true;
    }

    internal void PublishExecutable(
        in CommandRecordingDependencySignature dependencyIdentity,
        IReadOnlyList<KeyValuePair<VulkanResourceLifetimeKey, ulong>> dependencies,
        ulong recordingGeneration,
        int queuedSubmissionCount,
        int recordedPrimaryReferenceCount)
    {
        DependencyIdentity = dependencyIdentity;
        RecordingGeneration = recordingGeneration;
        QueuedSubmissionCount = queuedSubmissionCount;
        RecordedPrimaryReferenceCount = recordedPrimaryReferenceCount;
        PublishReferencedResources(dependencies);
        State = EVulkanRecordedCommandArtifactState.Executable;
        InvalidationReason = EVulkanRecordedCommandArtifactInvalidationReason.None;
        AdvanceGeneration();
    }

    internal void Invalidate(
        EVulkanRecordedCommandArtifactInvalidationReason reason)
    {
        ClearPublishedRecording();
        State = NativeBuffer.Handle == 0
            ? EVulkanRecordedCommandArtifactState.Empty
            : EVulkanRecordedCommandArtifactState.Invalid;
        InvalidationReason = reason;
        AdvanceGeneration();
    }

    internal void MarkFailed()
    {
        ClearPublishedRecording();
        State = EVulkanRecordedCommandArtifactState.Failed;
        InvalidationReason =
            EVulkanRecordedCommandArtifactInvalidationReason.RecordingFailed;
        AdvanceGeneration();
    }

    internal VulkanRecordedCommandArtifactRetirement CaptureRetirement()
    {
        CommandBuffer nativeBuffer = NativeBuffer;
        CommandPool ownerPool = OwnerPool;
        bool ownsPool = OwnsPool;
        ulong arenaOwnerIdentity = ArenaOwnerIdentity;
        ulong recordingGeneration = RecordingGeneration;
        CommandRecordingDependencySignature dependencyIdentity =
            DependencyIdentity;
        ulong referencedResourceIdentity = ReferencedResourceIdentity;
        int queuedSubmissionCount = QueuedSubmissionCount;
        int recordedPrimaryReferenceCount = RecordedPrimaryReferenceCount;

        ClearPublishedRecording();
        State = EVulkanRecordedCommandArtifactState.PendingRetirement;
        InvalidationReason =
            EVulkanRecordedCommandArtifactInvalidationReason.RetirementRequested;
        AdvanceGeneration();

        return new VulkanRecordedCommandArtifactRetirement(
            nativeBuffer,
            Level,
            ownerPool,
            ownsPool,
            FrameSlot,
            arenaOwnerIdentity,
            Generation,
            recordingGeneration,
            dependencyIdentity,
            referencedResourceIdentity,
            queuedSubmissionCount,
            recordedPrimaryReferenceCount);
    }

    internal void MarkRetired()
    {
        WorkerArenaOwner?.Detach(this);
        NativeBuffer = default;
        OwnerPool = default;
        OwnsPool = false;
        WorkerArenaOwner = null;
        RecordingGeneration = 0;
        DependencyIdentity = default;
        QueuedSubmissionCount = 0;
        RecordedPrimaryReferenceCount = 0;
        ClearPublishedRecording();
        State = EVulkanRecordedCommandArtifactState.Retired;
        InvalidationReason =
            EVulkanRecordedCommandArtifactInvalidationReason.Retired;
        AdvanceGeneration();
    }

    private void PublishReferencedResources(
        IReadOnlyList<KeyValuePair<VulkanResourceLifetimeKey, ulong>> dependencies)
    {
        ulong xorIdentity = 0;
        ulong sumIdentity = 0;
        // Command-buffer lifetime records publish a snapshot of their dependency
        // dictionary, so every key is already unique. Keeping a second HashSet on
        // every command-chain artifact allocated thousands of 64-entry tables while
        // entering a new Sponza view, even though the artifact retains only this
        // aggregate identity.
        for (int i = 0; i < dependencies.Count; i++)
        {
            KeyValuePair<VulkanResourceLifetimeKey, ulong> dependency =
                dependencies[i];
            VulkanRecordedResourceReference reference = new(
                dependency.Key.Type,
                dependency.Key.Handle,
                dependency.Value);

            FrameOpSignatureHasher itemIdentity = new();
            itemIdentity.Add((int)reference.Type);
            itemIdentity.Add(reference.Handle);
            itemIdentity.Add(reference.Generation);
            ulong itemHash = itemIdentity.ToHash();
            xorIdentity ^= itemHash;
            sumIdentity = unchecked(sumIdentity + itemHash);
        }

        FrameOpSignatureHasher identity = new();
        _referencedResourceCount = dependencies.Count;
        identity.Add(_referencedResourceCount);
        identity.Add(xorIdentity);
        identity.Add(sumIdentity);
        ReferencedResourceIdentity = identity.ToHash();
    }

    private void ClearPublishedRecording()
    {
        _referencedResourceCount = 0;
        ReferencedResourceIdentity = 0;
        HasInheritance = false;
        Inheritance = default;
    }

    private void AdvanceGeneration()
    {
        Generation = VulkanGeneration.NextNonZero(Generation);
        mutationAuthority?.NotifyArtifactMutation();
    }
}
