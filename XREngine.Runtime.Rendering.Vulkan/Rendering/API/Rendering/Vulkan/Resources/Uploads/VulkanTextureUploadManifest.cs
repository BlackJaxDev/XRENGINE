namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen set of uploads required by one PresentNow frame.</summary>
internal sealed class VulkanTextureUploadManifest
{
    private const int Capacity = VulkanAcceptedFramePlan.UploadCapacity;
    private const int IndexCapacity = Capacity * 2;
    private const int IndexMask = IndexCapacity - 1;
    private readonly VulkanTextureUploadTicket[] _tickets = new VulkanTextureUploadTicket[Capacity];
    private readonly XRTexture2D?[] _textures = new XRTexture2D?[Capacity];
    private readonly EVulkanFrameDependencyState[] _states =
        new EVulkanFrameDependencyState[Capacity];
    private readonly ulong[] _timelineValues = new ulong[Capacity];
    private readonly string?[] _failureDetails = new string?[Capacity];
    private readonly EVulkanPresentNowFailureDisposition[] _failureDispositions =
        new EVulkanPresentNowFailureDisposition[Capacity];
    private readonly int[] _index = new int[IndexCapacity];
    private readonly int[] _indexSlots = new int[Capacity];
    private readonly XRTexture2D?[] _unresolvedTextures =
        new XRTexture2D?[Capacity];
    private readonly long[] _unresolvedGenerations = new long[Capacity];
    private readonly VulkanTextureUploadGenerationRecord?[] _pinnedRecords =
        new VulkanTextureUploadGenerationRecord?[Capacity];
    private readonly VulkanTextureUploadGenerationEntry?[] _pinnedEntries =
        new VulkanTextureUploadGenerationEntry?[Capacity];
    private int _count;
    private int _indexSlotCount;
    private int _unresolvedCount;
    private int _pinnedCount;
    private string? _captureFailureDetail;
    private EVulkanPresentNowFailureDisposition _captureFailureDisposition;
    private ulong _progressVersion;

    internal bool RequiresExactDescriptorPublication { get; private set; }

    public bool IsEmpty =>
        _count == 0 &&
        _unresolvedCount == 0 &&
        _captureFailureDetail is null;
    internal int Count => _count;
    /// <summary>
    /// Monotonic version advanced only by durable manifest mutations. Foreground
    /// readiness uses this as liveness proof instead of counting poll iterations.
    /// </summary>
    internal ulong ProgressVersion => _progressVersion;
    internal bool AreAllReady
    {
        get
        {
            if (_unresolvedCount != 0 || _captureFailureDetail is not null)
                return false;
            for (int index = 0; index < _count; index++)
                if (_states[index] != EVulkanFrameDependencyState.Ready)
                    return false;
            return true;
        }
    }

    internal void BeginCapture(
        bool requireExactDescriptorPublication = false)
    {
        ReleaseGenerationPins();
        _tickets.AsSpan(0, _count).Clear();
        _textures.AsSpan(0, _count).Clear();
        _states.AsSpan(0, _count).Clear();
        _timelineValues.AsSpan(0, _count).Clear();
        _failureDetails.AsSpan(0, _count).Clear();
        _failureDispositions.AsSpan(0, _count).Clear();
        _unresolvedTextures.AsSpan(0, _unresolvedCount).Clear();
        _unresolvedGenerations.AsSpan(0, _unresolvedCount).Clear();
        for (int index = 0; index < _indexSlotCount; index++)
            _index[_indexSlots[index]] = 0;
        _count = 0;
        _indexSlotCount = 0;
        _unresolvedCount = 0;
        _captureFailureDetail = null;
        _captureFailureDisposition =
            EVulkanPresentNowFailureDisposition.RendererTerminal;
        _progressVersion = 0UL;
        RequiresExactDescriptorPublication = requireExactDescriptorPublication;
    }

    /// <summary>
    /// Pins exact ledger truth while this manifest can still query it. The
    /// caller holds <paramref name="record"/>'s synchronization lock.
    /// </summary>
    internal void PinGenerationNoLock(
        VulkanTextureUploadGenerationRecord record,
        VulkanTextureUploadGenerationEntry entry)
    {
        for (int index = 0; index < _pinnedCount; index++)
            if (ReferenceEquals(_pinnedEntries[index], entry))
                return;
        if (_pinnedCount >= _pinnedEntries.Length)
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Upload,
                _pinnedEntries.Length,
                _pinnedCount + 1);
        }

        entry.PinCount++;
        _pinnedRecords[_pinnedCount] = record;
        _pinnedEntries[_pinnedCount] = entry;
        _pinnedCount++;
    }

    private void ReleaseGenerationPins()
    {
        for (int index = 0; index < _pinnedCount; index++)
        {
            VulkanTextureUploadGenerationRecord? record = _pinnedRecords[index];
            VulkanTextureUploadGenerationEntry? entry = _pinnedEntries[index];
            if (record is not null && entry is not null)
            {
                using (VulkanFrameLockScope.Enter(
                           record.Sync,
                           EVulkanFrameWaitReason.UploadLock))
                {
                    if (entry.PinCount > 0)
                        entry.PinCount--;
                }
            }
            _pinnedRecords[index] = null;
            _pinnedEntries[index] = null;
        }
        _pinnedCount = 0;
    }

    internal void Add(
        in VulkanTextureUploadTicket ticket,
        XRTexture2D? texture = null)
    {
        if (!ticket.IsValid)
            return;
        if (TryFindIndex(in ticket, out int existingIndex, out _))
        {
            if (_textures[existingIndex] is null && texture is not null)
            {
                _textures[existingIndex] = texture;
                AdvanceProgress();
            }
            return;
        }
        if (_count == _tickets.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Upload,
                _tickets.Length,
                _count + 1);

        _ = TryFindIndex(in ticket, out _, out int slot);
        _tickets[_count] = ticket;
        _textures[_count] = texture;
        _states[_count] = EVulkanFrameDependencyState.Declared;
        _index[slot] = _count + 1;
        _indexSlots[_indexSlotCount++] = slot;
        _count++;
        AdvanceProgress();
    }

    internal void AddUnresolved(XRTexture2D texture, long generation)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (generation <= 0L)
            throw new ArgumentOutOfRangeException(nameof(generation));
        for (int index = 0; index < _unresolvedCount; index++)
        {
            if (ReferenceEquals(_unresolvedTextures[index], texture) &&
                _unresolvedGenerations[index] == generation)
            {
                return;
            }
        }
        if (_unresolvedCount >= _unresolvedTextures.Length)
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.Upload,
                _unresolvedTextures.Length,
                _unresolvedCount + 1);
        }

        _unresolvedTextures[_unresolvedCount] = texture;
        _unresolvedGenerations[_unresolvedCount] = generation;
        _unresolvedCount++;
        AdvanceProgress();
    }

    internal int UnresolvedCount => _unresolvedCount;

    internal bool TryGetUnresolved(
        int index,
        out XRTexture2D? texture,
        out long generation)
    {
        if ((uint)index >= (uint)_unresolvedCount)
        {
            texture = null;
            generation = 0L;
            return false;
        }

        texture = _unresolvedTextures[index];
        generation = _unresolvedGenerations[index];
        return texture is not null;
    }

    internal void ResolveUnresolved(int index)
    {
        if ((uint)index >= (uint)_unresolvedCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        int last = --_unresolvedCount;
        if (index != last)
        {
            _unresolvedTextures[index] = _unresolvedTextures[last];
            _unresolvedGenerations[index] = _unresolvedGenerations[last];
        }
        _unresolvedTextures[last] = null;
        _unresolvedGenerations[last] = 0L;
        AdvanceProgress();
    }

    internal XRTexture2D? GetTexture(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _textures[index];
    }

    internal void ApplyDurableState(
        in VulkanTextureUploadTicket ticket,
        EVulkanFrameDependencyState state,
        string? failureDetail,
        EVulkanPresentNowFailureDisposition failureDisposition =
            EVulkanPresentNowFailureDisposition.RendererTerminal)
    {
        switch (state)
        {
            case EVulkanFrameDependencyState.CpuPrepared:
                _ = MarkCpuPrepared(ticket);
                break;
            case EVulkanFrameDependencyState.GpuSubmitted:
                _ = MarkCpuPrepared(ticket);
                _ = MarkGpuSubmitted(ticket);
                break;
            case EVulkanFrameDependencyState.Ready:
                _ = MarkCpuPrepared(ticket);
                _ = MarkGpuSubmitted(ticket);
                _ = MarkReady(ticket);
                break;
            case EVulkanFrameDependencyState.TerminalFailed:
                _ = Fail(
                    ticket,
                    failureDetail ??
                        "Required texture upload generation failed without a diagnostic.",
                    failureDisposition);
                break;
        }
    }

    internal void FailCapture(
        string detail,
        EVulkanPresentNowFailureDisposition disposition =
            EVulkanPresentNowFailureDisposition.RendererTerminal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (_captureFailureDetail is not null)
            return;

        _captureFailureDetail = detail;
        _captureFailureDisposition = disposition;
        AdvanceProgress();
    }

    public bool Contains(in VulkanTextureUploadTicket ticket)
        => ticket.IsValid && TryFindIndex(in ticket, out _, out _);

    internal bool MarkCpuPrepared(in VulkanTextureUploadTicket ticket)
    {
        if (!TryFindIndex(in ticket, out int index, out _) ||
            _states[index] != EVulkanFrameDependencyState.Declared)
        {
            return false;
        }

        _states[index] = EVulkanFrameDependencyState.CpuPrepared;
        AdvanceProgress();
        return true;
    }

    internal bool MarkGpuSubmitted(
        in VulkanTextureUploadTicket ticket,
        ulong timelineValue = 0UL)
    {
        if (!TryFindIndex(in ticket, out int index, out _))
            return false;
        if (_states[index] == EVulkanFrameDependencyState.Declared)
            _states[index] = EVulkanFrameDependencyState.CpuPrepared;
        if (_states[index] != EVulkanFrameDependencyState.CpuPrepared)
            return false;

        // Zero is intentional for the dedicated-transfer fence path. A
        // nonzero value is reserved for an actual Vulkan timeline receipt.
        _timelineValues[index] = timelineValue;
        _states[index] = EVulkanFrameDependencyState.GpuSubmitted;
        AdvanceProgress();
        return true;
    }

    internal bool MarkReady(
        in VulkanTextureUploadTicket ticket,
        ulong timelineValue = 0UL)
    {
        if (!TryFindIndex(in ticket, out int index, out _) ||
            _states[index] is EVulkanFrameDependencyState.Ready or
                EVulkanFrameDependencyState.TerminalFailed)
        {
            return false;
        }

        if (_states[index] is not (
                EVulkanFrameDependencyState.Declared or
                EVulkanFrameDependencyState.CpuPrepared or
                EVulkanFrameDependencyState.GpuSubmitted))
        {
            return false;
        }
        if (_states[index] == EVulkanFrameDependencyState.Declared)
            _states[index] = EVulkanFrameDependencyState.CpuPrepared;
        if (_states[index] == EVulkanFrameDependencyState.CpuPrepared)
            _states[index] = EVulkanFrameDependencyState.GpuSubmitted;

        // A host-side ready proof commonly carries zero. Never let that erase
        // the real queue receipt already captured at GpuSubmitted.
        if (timelineValue != 0UL)
            _timelineValues[index] = timelineValue;
        _states[index] = EVulkanFrameDependencyState.Ready;
        AdvanceProgress();
        return true;
    }

    internal bool Fail(
        in VulkanTextureUploadTicket ticket,
        string detail,
        EVulkanPresentNowFailureDisposition disposition =
            EVulkanPresentNowFailureDisposition.RendererTerminal)
    {
        if (!TryFindIndex(in ticket, out int index, out _) ||
            _states[index] is EVulkanFrameDependencyState.Ready or
                EVulkanFrameDependencyState.TerminalFailed)
        {
            return false;
        }

        _failureDetails[index] = detail;
        _failureDispositions[index] = disposition;
        _states[index] = EVulkanFrameDependencyState.TerminalFailed;
        AdvanceProgress();
        return true;
    }

    internal void FailUnresolved(
        string detail,
        EVulkanPresentNowFailureDisposition disposition =
            EVulkanPresentNowFailureDisposition.RendererTerminal)
    {
        if (_unresolvedCount != 0)
            FailCapture(detail, disposition);
        for (int index = 0; index < _count; index++)
        {
            if (_states[index] is EVulkanFrameDependencyState.Ready or
                EVulkanFrameDependencyState.TerminalFailed)
            {
                continue;
            }

            _failureDetails[index] = detail;
            _failureDispositions[index] = disposition;
            _states[index] = EVulkanFrameDependencyState.TerminalFailed;
            AdvanceProgress();
        }
    }

    internal bool TryGetState(
        in VulkanTextureUploadTicket ticket,
        out EVulkanFrameDependencyState state,
        out ulong timelineValue,
        out string? failureDetail)
    {
        if (!TryFindIndex(in ticket, out int index, out _))
        {
            state = default;
            timelineValue = 0UL;
            failureDetail = null;
            return false;
        }

        state = _states[index];
        timelineValue = _timelineValues[index];
        failureDetail = _failureDetails[index];
        return true;
    }

    internal bool TryGetTerminalFailure(
        out VulkanTextureUploadTicket ticket,
        out string detail,
        out EVulkanPresentNowFailureDisposition disposition)
    {
        if (_captureFailureDetail is not null)
        {
            ticket = default;
            detail = _captureFailureDetail;
            disposition = _captureFailureDisposition;
            return true;
        }

        for (int index = 0; index < _count; index++)
        {
            if (_states[index] != EVulkanFrameDependencyState.TerminalFailed)
                continue;
            ticket = _tickets[index];
            detail = _failureDetails[index] ??
                "Required texture upload failed without a diagnostic.";
            disposition = _failureDispositions[index];
            return true;
        }

        ticket = default;
        detail = string.Empty;
        disposition = EVulkanPresentNowFailureDisposition.RendererTerminal;
        return false;
    }

    internal ref readonly VulkanTextureUploadTicket GetTicket(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return ref _tickets[index];
    }

    private bool TryFindIndex(
        in VulkanTextureUploadTicket ticket,
        out int ticketIndex,
        out int emptySlot)
    {
        int slot = (int)(HashTicket(in ticket) & IndexMask);
        for (int probe = 0; probe < IndexCapacity; probe++)
        {
            int storedIndex = _index[slot];
            if (storedIndex == 0)
            {
                ticketIndex = -1;
                emptySlot = slot;
                return false;
            }

            int index = storedIndex - 1;
            if (_tickets[index] == ticket)
            {
                ticketIndex = index;
                emptySlot = slot;
                return true;
            }

            slot = (slot + 1) & IndexMask;
        }

        ticketIndex = -1;
        emptySlot = -1;
        return false;
    }

    private static ulong HashTicket(in VulkanTextureUploadTicket ticket)
    {
        ulong hash = unchecked((ulong)ticket.Sequence) ^
            (unchecked((ulong)ticket.StreamingGeneration) +
             0x9E3779B97F4A7C15UL);
        hash ^= hash >> 30;
        hash *= 0xBF58476D1CE4E5B9UL;
        hash ^= hash >> 27;
        hash *= 0x94D049BB133111EBUL;
        return hash ^ (hash >> 31);
    }

    private void AdvanceProgress()
        => _progressVersion = unchecked(_progressVersion + 1UL);
}
