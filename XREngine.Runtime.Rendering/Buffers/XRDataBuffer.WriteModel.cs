using MemoryPack;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRDataBuffer
{
    private const int DefaultDirtyRangeCollapseThreshold = 8;
    private const float DefaultDirtyRangeFullUploadCoverageThreshold = 0.75f;

    [YamlIgnore]
    [MemoryPackIgnore]
    private readonly object _writeModelSync = new();

    [YamlIgnore]
    [MemoryPackIgnore]
    private readonly List<XRBufferDirtyRange> _dirtyRanges = [];

    [YamlIgnore]
    [MemoryPackIgnore]
    private ulong _revision;

    [YamlIgnore]
    [MemoryPackIgnore]
    private ulong _uploadedRevision;

    [YamlIgnore]
    [MemoryPackIgnore]
    private bool _pendingWriterUpload;

    [YamlIgnore]
    [MemoryPackIgnore]
    private XRBufferResolvedRoute _lastResolvedRoute = XRBufferResolvedRoute.Unknown;

    [YamlIgnore]
    [MemoryPackIgnore]
    private string _lastDeviceAddressDowngradeReason = "No backend has reported a GPU device address.";

    [YamlIgnore]
    [MemoryPackIgnore]
    private bool _syncingMemoryPolicyAndUsage;

    [YamlIgnore]
    [MemoryPackIgnore]
    private bool _defaultMemoryPolicyExplicit;

    private XRBufferMemoryPolicy _defaultMemoryPolicy = XRBufferMemoryPolicy.GpuOnly;
    private XRBufferWriteMode _defaultWriteMode = XRBufferWriteMode.Preserve;
    private XRBufferCpuAccess _defaultCpuAccess = XRBufferCpuAccess.Write;
    private XRBufferWriterDisposeBehavior _defaultWriterDisposeBehavior = XRBufferWriterDisposeBehavior.Commit;
    private uint _defaultAlignmentBytes = 1u;
    private bool _defaultKeepCpuMirror = true;
    private int _dirtyRangeCollapseThreshold = DefaultDirtyRangeCollapseThreshold;
    private float _dirtyRangeFullUploadCoverageThreshold = DefaultDirtyRangeFullUploadCoverageThreshold;

    /// <summary>
    /// Default memory policy used by scoped writers. This stays reconciled with <see cref="Usage"/>
    /// unless explicitly set, so callers do not have two independent intent knobs.
    /// </summary>
    public XRBufferMemoryPolicy DefaultMemoryPolicy
    {
        get => _defaultMemoryPolicy;
        set
        {
            _defaultMemoryPolicyExplicit = true;
            if (!SetField(ref _defaultMemoryPolicy, value))
                return;

            if (_syncingMemoryPolicyAndUsage)
                return;

            _syncingMemoryPolicyAndUsage = true;
            Usage = XRBufferPolicyResolver.ToUsage(value, _usage);
            _syncingMemoryPolicyAndUsage = false;
        }
    }

    public XRBufferWriteMode DefaultWriteMode
    {
        get => _defaultWriteMode;
        set => SetField(ref _defaultWriteMode, value);
    }

    public XRBufferCpuAccess DefaultCpuAccess
    {
        get => _defaultCpuAccess;
        set => SetField(ref _defaultCpuAccess, value);
    }

    public XRBufferWriterDisposeBehavior DefaultWriterDisposeBehavior
    {
        get => _defaultWriterDisposeBehavior;
        set => SetField(ref _defaultWriterDisposeBehavior, value);
    }

    public uint DefaultAlignmentBytes
    {
        get => _defaultAlignmentBytes;
        set => SetField(ref _defaultAlignmentBytes, Math.Max(1u, value));
    }

    public bool DefaultKeepCpuMirror
    {
        get => _defaultKeepCpuMirror;
        set => SetField(ref _defaultKeepCpuMirror, value);
    }

    public int DirtyRangeCollapseThreshold
    {
        get => _dirtyRangeCollapseThreshold;
        set => SetField(ref _dirtyRangeCollapseThreshold, Math.Max(1, value));
    }

    public float DirtyRangeFullUploadCoverageThreshold
    {
        get => _dirtyRangeFullUploadCoverageThreshold;
        set => SetField(ref _dirtyRangeFullUploadCoverageThreshold, Math.Clamp(value, 0.01f, 1.0f));
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    public ulong Revision => _revision;

    [YamlIgnore]
    [MemoryPackIgnore]
    public ulong UploadedRevision => _uploadedRevision;

    [YamlIgnore]
    [MemoryPackIgnore]
    public bool HasPendingUpload
    {
        get
        {
            if (_pendingWriterUpload)
                return true;

            foreach (AbstractRenderAPIObject wrapper in APIWrappers)
                if (wrapper is IApiDataBuffer api && api.BackendHasPendingUpload)
                    return true;

            return false;
        }
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    public bool HasCpuMirror => _clientSideSource is not null && _clientSideSource.Address != VoidPtr.Zero;

    [YamlIgnore]
    [MemoryPackIgnore]
    public bool IsReadyForGpuUse
    {
        get
        {
            bool hasApiBuffer = false;
            foreach (AbstractRenderAPIObject wrapper in APIWrappers)
            {
                if (wrapper is not IApiDataBuffer api)
                    continue;

                hasApiBuffer = true;
                if (!api.BackendIsReadyForGpuUse)
                    return false;
            }

            return hasApiBuffer ? true : !HasPendingUpload;
        }
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    public ulong BackendAllocatedByteSize
    {
        get
        {
            ulong max = _clientSideSource?.Length ?? Length;
            foreach (AbstractRenderAPIObject wrapper in APIWrappers)
                if (wrapper is IApiDataBuffer api)
                    max = Math.Max(max, api.BackendAllocatedByteSize);
            return max;
        }
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    public ulong BackendUploadedByteCount
    {
        get
        {
            ulong max = 0ul;
            foreach (AbstractRenderAPIObject wrapper in APIWrappers)
                if (wrapper is IApiDataBuffer api)
                    max = Math.Max(max, api.BackendUploadedByteCount);
            return max;
        }
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    public XRBufferResolvedRoute LastResolvedRoute => _lastResolvedRoute;

    [YamlIgnore]
    [MemoryPackIgnore]
    public int DirtyRangeCount
    {
        get
        {
            lock (_writeModelSync)
                return _dirtyRanges.Count;
        }
    }

    public XRBufferDirtyRange[] GetDirtyRangesSnapshot()
    {
        lock (_writeModelSync)
            return [.. _dirtyRanges];
    }

    public XRBufferStateSnapshot GetStateSnapshot()
    {
        bool generated = false;
        bool persistent = false;
        XRBufferResolvedRoute route = _lastResolvedRoute;
        foreach (AbstractRenderAPIObject wrapper in APIWrappers)
        {
            generated |= wrapper.IsGenerated;
            if (wrapper is not IApiDataBuffer api)
                continue;

            persistent |= api.BackendIsPersistentlyMapped;
            if (api.BackendResolvedRoute != XRBufferResolvedRoute.Unknown)
                route = api.BackendResolvedRoute;
        }

        bool hasAddress = TryGetGpuAddress(out _, out string downgradeReason);
        return new XRBufferStateSnapshot(
            generated,
            BackendAllocatedByteSize,
            BackendUploadedByteCount,
            Revision,
            UploadedRevision,
            HasPendingUpload,
            DefaultMemoryPolicy,
            route,
            persistent || IsMapped,
            HasCpuMirror,
            hasAddress,
            downgradeReason,
            IsDescriptorBindingReady(),
            IsReadyForGpuUse,
            DirtyRangeCount);
    }

    public XRBufferWriter<T> Alloc<T>(uint count) where T : unmanaged
        => Alloc<T>(count, XRBufferWriteOptions.FromBuffer(this));

    public XRBufferWriter<T> Alloc<T>(uint count, XRBufferWriteMode mode) where T : unmanaged
        => Alloc<T>(count, XRBufferWriteOptions.FromBuffer(this).WithWriteMode(mode));

    public XRBufferWriter<T> Alloc<T>(uint count, XRBufferWriteOptions options) where T : unmanaged
        => AllocCore<T>(
            options.WriteMode == XRBufferWriteMode.Append ? ElementCount : 0u,
            count,
            options);

    public XRBufferWriter<T> AllocAt<T>(uint elementOffset, uint count) where T : unmanaged
        => AllocAt<T>(elementOffset, count, XRBufferWriteOptions.FromBuffer(this));

    public XRBufferWriter<T> AllocAt<T>(uint elementOffset, uint count, XRBufferWriteOptions options) where T : unmanaged
        => AllocCore<T>(elementOffset, count, options);

    private unsafe XRBufferWriter<T> AllocCore<T>(uint elementOffset, uint count, XRBufferWriteOptions options) where T : unmanaged
    {
        EnsureWritableRegion<T>(elementOffset, count, options, out DataSource source);
        uint elementSize = (uint)Unsafe.SizeOf<T>();
        ulong byteOffset = (ulong)elementOffset * elementSize;
        if (byteOffset > int.MaxValue || count > int.MaxValue)
            throw new InvalidOperationException($"Buffer '{AttributeName}' writer range is too large for a Span<T>.");

        Span<T> span = count == 0u
            ? Span<T>.Empty
            : new Span<T>((source.Address + (uint)byteOffset).Pointer, checked((int)count));
        if (options.ClearOnAllocate)
            span.Clear();

        return new XRBufferWriter<T>(this, span, elementOffset, count, options);
    }

    public XRBufferWriter<byte> AllocBytes(uint byteCount)
        => Alloc<byte>(byteCount, XRBufferWriteOptions.FromBuffer(this));

    public XRBufferWriter<byte> AllocBytes(uint byteCount, XRBufferWriteMode mode)
        => Alloc<byte>(byteCount, XRBufferWriteOptions.FromBuffer(this).WithWriteMode(mode));

    public XRBufferWriter<byte> AllocBytes(uint byteCount, XRBufferWriteOptions options)
        => Alloc<byte>(byteCount, options);

    /// <summary>
    /// Commits a dirty range using the buffer's current element layout. This is intended for
    /// legacy/custom CPU writers that already wrote into <see cref="ClientSideSource"/> without
    /// needing a typed scoped writer to reinterpret the buffer.
    /// </summary>
    public void CommitDirtyElements(uint elementOffset, uint elementCount)
        => CommitDirtyElements(elementOffset, elementCount, XRBufferWriteOptions.FromBuffer(this));

    public void CommitDirtyElements(uint elementOffset, uint elementCount, XRBufferWriteOptions options)
    {
        if (elementCount == 0u)
            return;

        ulong byteOffset = (ulong)elementOffset * ElementSize;
        ulong byteLength = (ulong)elementCount * ElementSize;
        if (byteOffset > uint.MaxValue || byteLength > uint.MaxValue)
            throw new InvalidOperationException($"Buffer '{AttributeName}' dirty element range exceeds supported byte range.");

        CommitDirtyBytes((uint)byteOffset, (uint)byteLength, options);
    }

    /// <summary>
    /// Commits a byte range after CPU-side writes performed by code that cannot use a typed writer.
    /// </summary>
    public void CommitDirtyBytes(uint byteOffset, uint byteLength)
        => CommitDirtyBytes(byteOffset, byteLength, XRBufferWriteOptions.FromBuffer(this));

    public void CommitDirtyBytes(uint byteOffset, uint byteLength, XRBufferWriteOptions options)
    {
        if (byteLength == 0u)
            return;

        Span<XRBufferDirtyRange> ranges = stackalloc XRBufferDirtyRange[1];
        ranges[0] = new XRBufferDirtyRange(byteOffset, byteLength);
        CommitDirtyByteRanges(ranges, options);
    }

    public XRBufferReadbackTicket RequestReadback(uint byteOffset, uint byteCount, bool diagnostic = false)
    {
        if (byteOffset > Length || byteCount > Length - byteOffset)
        {
            return new XRBufferReadbackTicket(
                this,
                byteOffset,
                byteCount,
                Revision,
                XRBufferReadbackTicketStatus.Rejected,
                $"Readback range {byteOffset}+{byteCount} exceeds buffer length {Length}.");
        }

        bool readbackPolicy = DefaultMemoryPolicy is XRBufferMemoryPolicy.GpuToCpuReadback or XRBufferMemoryPolicy.CpuGpuSharedDiagnostic;
        if (!readbackPolicy && !diagnostic)
        {
            XRBufferWriteTelemetry.RecordZeroReadbackViolation();
            return new XRBufferReadbackTicket(
                this,
                byteOffset,
                byteCount,
                Revision,
                XRBufferReadbackTicketStatus.Rejected,
                $"Buffer '{AttributeName}' policy {DefaultMemoryPolicy} rejects production CPU readback.");
        }

        XRBufferReadbackTicket ticket = new(
            this,
            byteOffset,
            byteCount,
            Revision,
            XRBufferReadbackTicketStatus.Pending,
            "Waiting for backend readback completion.");

        if (diagnostic && HasCpuMirror)
            ticket.TryCompleteFromCpuMirror("DiagnosticCpuMirror");

        return ticket;
    }

    public bool TryGetGpuAddress(out ulong address, out string downgradeReason)
    {
        foreach (AbstractRenderAPIObject wrapper in APIWrappers)
        {
            if (wrapper is not IApiDataBuffer api)
                continue;

            if (api.TryGetGpuAddress(out address, out downgradeReason))
            {
                _lastDeviceAddressDowngradeReason = string.Empty;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(downgradeReason))
                _lastDeviceAddressDowngradeReason = downgradeReason;
        }

        address = 0ul;
        downgradeReason = string.IsNullOrWhiteSpace(_lastDeviceAddressDowngradeReason)
            ? "No backend reported a GPU device address; OpenGL and classic binding paths do not expose fake addresses."
            : _lastDeviceAddressDowngradeReason;
        return false;
    }

    internal void CommitWriterRanges<T>(
        uint elementOffset,
        uint elementCount,
        IReadOnlyList<XRBufferDirtyRange>? elementDirtyRanges,
        XRBufferWriteOptions options) where T : unmanaged
    {
        if (elementCount == 0u && (elementDirtyRanges is null || elementDirtyRanges.Count == 0))
            return;

        uint elementSize = (uint)Unsafe.SizeOf<T>();
        if (elementDirtyRanges is null || elementDirtyRanges.Count == 0)
        {
            ulong dirtyByteOffset64 = (ulong)elementOffset * elementSize;
            ulong dirtyByteLength64 = (ulong)elementCount * elementSize;
            if (dirtyByteOffset64 > uint.MaxValue || dirtyByteLength64 > uint.MaxValue)
                throw new InvalidOperationException($"Buffer '{AttributeName}' dirty range exceeds supported byte range.");

            Span<XRBufferDirtyRange> ranges = stackalloc XRBufferDirtyRange[1];
            ranges[0] = new XRBufferDirtyRange((uint)dirtyByteOffset64, (uint)dirtyByteLength64);
            CommitDirtyByteRanges(ranges, options);
            return;
        }

        XRBufferDirtyRange[] byteRanges = new XRBufferDirtyRange[elementDirtyRanges.Count];
        for (int i = 0; i < elementDirtyRanges.Count; i++)
        {
            XRBufferDirtyRange elementRange = elementDirtyRanges[i];
            ulong dirtyByteOffset64 = ((ulong)elementOffset + elementRange.OffsetBytes) * elementSize;
            ulong dirtyByteLength64 = (ulong)elementRange.LengthBytes * elementSize;
            if (dirtyByteOffset64 > uint.MaxValue || dirtyByteLength64 > uint.MaxValue)
                throw new InvalidOperationException($"Buffer '{AttributeName}' dirty range exceeds supported byte range.");

            byteRanges[i] = new XRBufferDirtyRange((uint)dirtyByteOffset64, (uint)dirtyByteLength64);
        }

        CommitDirtyByteRanges(byteRanges, options);
    }

    internal void ReconcileDefaultMemoryPolicyFromLegacyHints()
    {
        if (_defaultMemoryPolicyExplicit || _syncingMemoryPolicyAndUsage)
            return;

        XRBufferMemoryPolicy policy = XRBufferPolicyResolver.FromUsage(_usage, _mapped, _storageFlags, _rangeFlags);
        SetField(ref _defaultMemoryPolicy, policy, nameof(DefaultMemoryPolicy));
    }

    internal void ReportBackendUploadState(
        ulong allocatedBytes,
        ulong uploadedBytes,
        bool hasPendingUpload,
        XRBufferResolvedRoute resolvedRoute,
        bool readyForGpuUse)
    {
        lock (_writeModelSync)
        {
            _pendingWriterUpload = hasPendingUpload || !readyForGpuUse;
            if (!hasPendingUpload && readyForGpuUse && uploadedBytes >= (ulong)Length)
                _uploadedRevision = _revision;
            if (resolvedRoute != XRBufferResolvedRoute.Unknown)
                _lastResolvedRoute = resolvedRoute;
        }
    }

    internal void ReportDeviceAddressDowngrade(string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
            _lastDeviceAddressDowngradeReason = reason;
    }

    private unsafe void EnsureWritableRegion<T>(
        uint elementOffset,
        uint elementCount,
        XRBufferWriteOptions options,
        out DataSource source) where T : unmanaged
    {
        ConfigureRawComponentLayout<T>(out _componentType, out _componentCount);
        _normalize = false;

        ulong requiredElements64 = (ulong)elementOffset + elementCount;
        if (requiredElements64 > uint.MaxValue)
            throw new InvalidOperationException($"Buffer '{AttributeName}' element count exceeds uint range.");

        uint requiredElements = Math.Max((uint)requiredElements64, 1u);
        bool copyExisting = options.WriteMode is XRBufferWriteMode.Preserve or XRBufferWriteMode.Append or XRBufferWriteMode.Scattered;
        bool resized = EnsureRawCapacityForWriter<T>(requiredElements, copyExisting, options.GrowCapacityGeometrically);
        if (!resized && options.WriteMode == XRBufferWriteMode.Discard && elementOffset == 0u && elementCount > 0u)
            _elementCount = requiredElements;

        source = _clientSideSource ?? throw new InvalidOperationException($"Failed to allocate client-side writer storage for buffer '{AttributeName}'.");
        if (options.AlignmentBytes > 1u && (((ulong)elementOffset * (uint)sizeof(T)) % options.AlignmentBytes) != 0ul)
            throw new InvalidOperationException($"Buffer '{AttributeName}' writer offset {elementOffset} violates {options.AlignmentBytes}-byte alignment.");
    }

    private unsafe bool EnsureRawCapacityForWriter<T>(uint requiredElementCount, bool copyExisting, bool growCapacityGeometrically) where T : unmanaged
    {
        uint requiredByteLength = GetRawByteLength<T>(requiredElementCount);
        if (_clientSideSource is not null && ElementCount >= requiredElementCount && _clientSideSource.Length >= requiredByteLength)
            return false;

        uint oldLength = _clientSideSource?.Length ?? 0u;
        uint newElementCount = growCapacityGeometrically ? XRMath.NextPowerOfTwo(requiredElementCount) : requiredElementCount;
        uint newByteLength = GetRawByteLength<T>(newElementCount);
        DataSource newSource = DataSource.Allocate(newByteLength, zeroMemory: true);
        if (copyExisting && _clientSideSource is not null && oldLength > 0u)
            Memory.Move(newSource.Address, _clientSideSource.Address, Math.Min(oldLength, newByteLength));

        _clientSideSource?.Dispose();
        _clientSideSource = newSource;
        _elementCount = newElementCount;
        ClearGpuCompressedPayload();
        return true;
    }

    private void CommitDirtyByteRanges(ReadOnlySpan<XRBufferDirtyRange> dirtyRanges, XRBufferWriteOptions options)
    {
        if (dirtyRanges.IsEmpty)
            return;

        bool hasDirtyBytes = false;
        uint capacity = _clientSideSource?.Length ?? Length;
        for (int i = 0; i < dirtyRanges.Length; i++)
        {
            XRBufferDirtyRange range = dirtyRanges[i];
            if (range.LengthBytes == 0u)
                continue;

            hasDirtyBytes = true;
            uint end = range.OffsetBytes + range.LengthBytes;
            if (range.OffsetBytes > capacity || end > capacity)
                throw new InvalidOperationException($"Buffer '{AttributeName}' dirty range {range.OffsetBytes}+{range.LengthBytes} exceeds allocated capacity {capacity}.");
        }

        if (!hasDirtyBytes)
            return;

        XRBufferDirtyRange[] rangesToUpload;
        lock (_writeModelSync)
        {
            _revision++;
            _pendingWriterUpload = true;
            ulong totalDirtyBytes = 0u;
            for (int i = 0; i < dirtyRanges.Length; i++)
                totalDirtyBytes += dirtyRanges[i].LengthBytes;

            _lastResolvedRoute = ResolveCompatibilityRoute(options, totalDirtyBytes > uint.MaxValue ? uint.MaxValue : (uint)totalDirtyBytes);
            _dirtyRanges.Clear();
            for (int i = 0; i < dirtyRanges.Length; i++)
                AddDirtyRangeLocked(dirtyRanges[i], capacity);
            rangesToUpload = [.. _dirtyRanges];
        }

        TraceWriterUploadCommit(options, rangesToUpload);
        ClearGpuCompressedPayload();
        for (int i = 0; i < rangesToUpload.Length; i++)
        {
            XRBufferDirtyRange range = rangesToUpload[i];
            XRBufferWriteTelemetry.RecordUpload(_lastResolvedRoute, range.LengthBytes);
            if (range.OffsetBytes == 0u && range.LengthBytes >= Length)
                PushData();
            else
                PushSubData(checked((int)range.OffsetBytes), range.LengthBytes);
        }
    }

    private void AddDirtyRangeLocked(XRBufferDirtyRange range, uint capacity)
    {
        if (range.LengthBytes == 0u)
            return;

        _dirtyRanges.Add(range);
        _dirtyRanges.Sort(static (a, b) => a.OffsetBytes.CompareTo(b.OffsetBytes));

        int writeIndex = 0;
        for (int readIndex = 0; readIndex < _dirtyRanges.Count; readIndex++)
        {
            XRBufferDirtyRange current = _dirtyRanges[readIndex];
            if (writeIndex == 0)
            {
                _dirtyRanges[writeIndex++] = current;
                continue;
            }

            XRBufferDirtyRange previous = _dirtyRanges[writeIndex - 1];
            if (previous.TouchesOrOverlaps(current))
            {
                _dirtyRanges[writeIndex - 1] = previous.Merge(current);
                continue;
            }

            _dirtyRanges[writeIndex++] = current;
        }

        if (writeIndex < _dirtyRanges.Count)
            _dirtyRanges.RemoveRange(writeIndex, _dirtyRanges.Count - writeIndex);

        ulong dirtyBytes = 0ul;
        for (int i = 0; i < _dirtyRanges.Count; i++)
            dirtyBytes += _dirtyRanges[i].LengthBytes;

        bool collapseByCount = _dirtyRanges.Count > DirtyRangeCollapseThreshold;
        bool collapseByCoverage = capacity > 0u && dirtyBytes >= (ulong)(capacity * DirtyRangeFullUploadCoverageThreshold);
        if (collapseByCount || collapseByCoverage)
        {
            _dirtyRanges.Clear();
            _dirtyRanges.Add(new XRBufferDirtyRange(0u, capacity));
            TraceDirtyRangeCollapse(collapseByCount, collapseByCoverage, dirtyBytes, capacity);
        }
    }

    private XRBufferResolvedRoute ResolveCompatibilityRoute(XRBufferWriteOptions options, uint dirtyByteLength)
    {
        XRBufferMemoryPolicy policy = options.MemoryPolicy;
        if (policy == default && DefaultMemoryPolicy != default)
            policy = DefaultMemoryPolicy;

        XRBufferResolvedRoute route = XRBufferPolicyResolver.ResolveOpenGL(policy, StorageFlags, RangeFlags, uploadQueueEnabled: true, dirtyByteLength);
        return route == XRBufferResolvedRoute.Unknown ? XRBufferResolvedRoute.CompatibilityPush : route;
    }

    private void TraceDirtyRangeCollapse(bool byCount, bool byCoverage, ulong dirtyBytes, uint capacity)
    {
        if (!RenderDiagnosticsFlags.PushSubDataTrace && !RenderDiagnosticsFlags.UploadStageLogging)
            return;

            RuntimeRenderObjectServices.Current?.LogOutput(
            $"[XRDataBuffer] Dirty ranges collapsed for '{AttributeName}': byCount={byCount} byCoverage={byCoverage} dirtyBytes={dirtyBytes} capacity={capacity} revision={Revision}.");
    }

    private void TraceWriterUploadCommit(XRBufferWriteOptions options, XRBufferDirtyRange[] rangesToUpload)
    {
        if (!RenderDiagnosticsFlags.PushSubDataTrace && !RenderDiagnosticsFlags.UploadStageLogging)
            return;

        ulong dirtyBytes = 0ul;
        for (int i = 0; i < rangesToUpload.Length; i++)
            dirtyBytes += rangesToUpload[i].LengthBytes;

        RuntimeRenderObjectServices.Current?.LogOutput(
            $"[XRDataBuffer] Commit '{AttributeName}': policy={options.MemoryPolicy} route={_lastResolvedRoute} bytes={dirtyBytes} dirtyRanges={rangesToUpload.Length} allocatedBytes={BackendAllocatedByteSize} uploadedRevision={UploadedRevision} revision={Revision} ready={IsReadyForGpuUse} pending={HasPendingUpload}.");
    }

    private bool IsDescriptorBindingReady()
        => BindingIndexOverride.HasValue ||
           !string.IsNullOrWhiteSpace(AttributeName) ||
           Target is EBufferTarget.DrawIndirectBuffer or EBufferTarget.DispatchIndirectBuffer or EBufferTarget.ElementArrayBuffer;

}
