using System.Diagnostics.CodeAnalysis;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Rendering;

/// <summary>
/// Copies collected mesh ownership into four reusable snapshots. Each candidate,
/// committed source, and asynchronous query retains its own lease token.
/// </summary>
public sealed class EditorPickingSnapshotRing
{
    private const int SlotCapacity = 4;
    private const int MaxRowsPerSnapshot = 65_536;

    private readonly object _sync = new();
    private readonly EditorPickingSnapshotSlot[] _slots;
    private readonly EditorPickingLeaseToken[] _tokens;
    private int _nextSlotIndex;
    private ulong _nextSlotGeneration;
    private ulong _nextTokenGeneration;

    public EditorPickingSnapshotRing(int initialRowCapacity = 256, int leaseCapacity = 32)
    {
        if (initialRowCapacity < 1 || initialRowCapacity > MaxRowsPerSnapshot)
            throw new ArgumentOutOfRangeException(nameof(initialRowCapacity));
        if (leaseCapacity < SlotCapacity)
            throw new ArgumentOutOfRangeException(nameof(leaseCapacity));

        _slots = new EditorPickingSnapshotSlot[SlotCapacity];
        for (int i = 0; i < _slots.Length; i++)
            _slots[i] = new EditorPickingSnapshotSlot(initialRowCapacity);
        _tokens = new EditorPickingLeaseToken[leaseCapacity];
    }

    /// <summary>
    /// Freezes every collected mesh command, including authored late commands.
    /// The caller must keep package command storage frozen during this call.
    /// Row storage grows only when previously unseen membership exceeds capacity.
    /// </summary>
    public bool TryCapture(
        BackendReadyFramePackage package,
        out EditorPickingSnapshotLease lease,
        out EditorPickingSnapshotFailure failure)
    {
        ArgumentNullException.ThrowIfNull(package);
        lease = default;
        failure = EditorPickingSnapshotFailure.None;
        if (package.State is not (EBackendReadyFramePackageState.Prepared or
            EBackendReadyFramePackageState.Published))
        {
            failure = EditorPickingSnapshotFailure.InvalidPackageState;
            return false;
        }

        lock (_sync)
        {
            int slotIndex = FindFreeSlotIndex();
            if (slotIndex < 0)
            {
                failure = EditorPickingSnapshotFailure.NoFreeSnapshot;
                return false;
            }

            int tokenIndex = FindFreeTokenIndex();
            if (tokenIndex < 0)
            {
                failure = EditorPickingSnapshotFailure.LeaseCapacityExhausted;
                return false;
            }

            EditorPickingSnapshotSlot slot = _slots[slotIndex];
            slot.ClearRows();
            ReadOnlySpan<BackendReadyRenderPass> passes = package.Passes;
            for (int passIndex = 0; passIndex < passes.Length; passIndex++)
            {
                BackendReadyRenderPass pass = passes[passIndex];
                for (int commandIndex = 0; commandIndex < pass.CommandCount; commandIndex++)
                {
                    if (package.GetPassCommand(in pass, commandIndex) is not RenderCommandMesh3D command)
                        continue;

                    uint key = command.StableQueryKey;
                    if (key == 0u)
                    {
                        failure = EditorPickingSnapshotFailure.InvalidStableQueryKey;
                        slot.ClearRows();
                        return false;
                    }

                    RenderInfo? renderInfo = command.OwnerRenderInfo;
                    var mesh = (renderInfo as RenderInfo3D)?.OwnerRenderableMesh;
                    if (slot.RowIndexByKey.TryGetValue(key, out int existingIndex))
                    {
                        EditorPickingSnapshotRow existing = slot.Rows[existingIndex];
                        if (!ReferenceEquals(existing.RenderInfo, renderInfo) ||
                            !ReferenceEquals(existing.RenderableMesh, mesh))
                        {
                            failure = EditorPickingSnapshotFailure.ConflictingOwner;
                            slot.ClearRows();
                            return false;
                        }

                        continue;
                    }

                    if (!EnsureRowCapacity(slot))
                    {
                        failure = EditorPickingSnapshotFailure.RowCapacityExceeded;
                        slot.ClearRows();
                        return false;
                    }

                    slot.RowIndexByKey.Add(key, slot.RowCount);
                    slot.Rows[slot.RowCount++] = new EditorPickingSnapshotRow(key, renderInfo, mesh);
                }
            }

            slot.Generation = checked(++_nextSlotGeneration);
            slot.PackageGeneration = package.PackageGeneration;
            slot.Identity = package.Identity;
            slot.RefCount = 1;
            _nextSlotIndex = (slotIndex + 1) % _slots.Length;
            lease = ActivateToken(tokenIndex, slotIndex, slot);
            return true;
        }
    }

    /// <summary>Retains a separate use for an asynchronous query or committed source.</summary>
    public bool TryRetain(
        in EditorPickingSnapshotLease source,
        out EditorPickingSnapshotLease retained,
        out EditorPickingSnapshotFailure failure)
    {
        lock (_sync)
        {
            retained = default;
            if (!TryGetValidSlot(in source, out EditorPickingSnapshotSlot? slot))
            {
                failure = EditorPickingSnapshotFailure.StaleLease;
                return false;
            }

            int tokenIndex = FindFreeTokenIndex();
            if (tokenIndex < 0)
            {
                failure = EditorPickingSnapshotFailure.LeaseCapacityExhausted;
                return false;
            }

            slot.RefCount++;
            retained = ActivateToken(tokenIndex, source.SlotIndex, slot);
            failure = EditorPickingSnapshotFailure.None;
            return true;
        }
    }

    /// <summary>
    /// Resolves an authored command through its retained snapshot. Native draw
    /// indices require the separate canonical scene publication.
    /// </summary>
    public bool TryResolve(
        in EditorPickingSnapshotLease source,
        in EditorPickingEncodedPixel pixel,
        ulong requestGeneration,
        uint viewIndex,
        out EditorPickingResult result)
    {
        result = EditorPickingResult.Miss(requestGeneration,
            source.PackageIdentity, source.PackageGeneration, viewIndex);
        if (!pixel.TryDecode(out uint key, out EditorPickingSourceKind sourceKind))
            return false;
        if (sourceKind != EditorPickingSourceKind.AuthoredCommand)
            return false;

        lock (_sync)
        {
            if (!TryGetValidSlot(in source, out EditorPickingSnapshotSlot? slot) ||
                !slot.RowIndexByKey.TryGetValue(key, out int rowIndex))
                return false;

            EditorPickingSnapshotRow row = slot.Rows[rowIndex];
            if (row.RenderInfo is null)
                return false;

            result = new EditorPickingResult(
                true,
                requestGeneration,
                slot.Identity,
                slot.PackageGeneration,
                viewIndex,
                sourceKind,
                key,
                row.RenderInfo,
                row.RenderableMesh);
            return true;
        }
    }

    internal bool IsLeaseValid(in EditorPickingSnapshotLease lease)
    {
        lock (_sync)
            return TryGetValidSlot(in lease, out _);
    }

    internal void Release(in EditorPickingSnapshotLease lease)
    {
        lock (_sync)
        {
            if (!TryGetValidSlot(in lease, out EditorPickingSnapshotSlot? slot))
                return;

            _tokens[lease.TokenIndex].Active = false;
            slot.RefCount--;
            if (slot.RefCount == 0)
                slot.ClearRows();
        }
    }

    private int FindFreeSlotIndex()
    {
        for (int offset = 0; offset < _slots.Length; offset++)
        {
            int index = (_nextSlotIndex + offset) % _slots.Length;
            if (_slots[index].RefCount == 0)
                return index;
        }

        return -1;
    }

    private int FindFreeTokenIndex()
    {
        for (int index = 0; index < _tokens.Length; index++)
        {
            if (!_tokens[index].Active)
                return index;
        }

        return -1;
    }

    private EditorPickingSnapshotLease ActivateToken(
        int tokenIndex,
        int slotIndex,
        EditorPickingSnapshotSlot slot)
    {
        ulong tokenGeneration = checked(++_nextTokenGeneration);
        _tokens[tokenIndex] = new EditorPickingLeaseToken
        {
            SlotIndex = slotIndex,
            SlotGeneration = slot.Generation,
            TokenGeneration = tokenGeneration,
            Active = true,
        };
        return new EditorPickingSnapshotLease(this, tokenIndex, tokenGeneration,
            slotIndex, slot.Generation, in slot.Identity, slot.PackageGeneration);
    }

    private bool TryGetValidSlot(
        in EditorPickingSnapshotLease lease,
        [NotNullWhen(true)] out EditorPickingSnapshotSlot? slot)
    {
        slot = null;
        if (!lease.BelongsTo(this) ||
            (uint)lease.TokenIndex >= (uint)_tokens.Length ||
            (uint)lease.SlotIndex >= (uint)_slots.Length)
            return false;

        EditorPickingLeaseToken token = _tokens[lease.TokenIndex];
        if (!token.Active ||
            token.TokenGeneration != lease.TokenGeneration ||
            token.SlotIndex != lease.SlotIndex ||
            token.SlotGeneration != lease.SlotGeneration)
            return false;

        EditorPickingSnapshotSlot candidate = _slots[lease.SlotIndex];
        if (candidate.RefCount <= 0 ||
            candidate.Generation != lease.SlotGeneration ||
            candidate.PackageGeneration != lease.PackageGeneration ||
            candidate.Identity != lease.PackageIdentity)
            return false;

        slot = candidate;
        return true;
    }

    private static bool EnsureRowCapacity(EditorPickingSnapshotSlot slot)
    {
        if (slot.RowCount < slot.Rows.Length)
            return true;
        if (slot.Rows.Length >= MaxRowsPerSnapshot)
            return false;

        int capacity = Math.Min(MaxRowsPerSnapshot, checked(slot.Rows.Length * 2));
        Array.Resize(ref slot.Rows, capacity);
        slot.RowIndexByKey.EnsureCapacity(capacity);
        return true;
    }
}
