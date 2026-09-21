using System;
using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>
/// Nonblocking GPU timestamp timing for one DDGI update sequence.
/// The owner must call <see cref="Begin"/> before ray generation, <see cref="End"/>
/// after the border updates, and <see cref="Resolve"/> on subsequent render frames.
/// </summary>
internal sealed class DDGIGpuTiming : IDisposable
{
    private readonly QueryPair[] _pairs;
    private IRuntimeRendererHost? _renderer;
    private int _openSlot = -1;
    private bool _disposed;

    /// <summary>Describes why timing is not currently yielding a usable measurement.</summary>
    public EDDGIGpuTimingAvailability Availability { get; private set; } = EDDGIGpuTimingAvailability.Unrequested;

    /// <summary>Diagnostic text suitable for a throttled caller-owned warning.</summary>
    public string? Diagnostic { get; private set; }

    public DDGIGpuTiming(int queryPairCount = 8)
    {
        if (queryPairCount is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(queryPairCount), "DDGI GPU timing supports a ring of four or eight query pairs.");

        _pairs = new QueryPair[queryPairCount];
        for (int i = 0; i < _pairs.Length; i++)
            _pairs[i] = new QueryPair();
    }

    /// <summary>
    /// Begins a timestamp pair. This method never waits for an unavailable result.
    /// </summary>
    public bool Begin(int scheduledProbes)
    {
        ThrowIfDisposed();
        if (scheduledProbes <= 0)
        {
            SetAvailability(EDDGIGpuTimingAvailability.InvalidWork, "DDGI GPU timing requires a positive scheduled probe count.");
            return false;
        }
        if (_openSlot >= 0)
        {
            SetAvailability(EDDGIGpuTimingAvailability.OpenScope, "DDGI GPU timing already has an open timestamp scope.");
            return false;
        }
        if (!TryGetCapability(out IRuntimeRendererHost? renderer, out IOcclusionQueryBackendCapability? capability))
        {
            SetAvailability(EDDGIGpuTimingAvailability.Unsupported, "The active renderer does not provide hardware timestamp queries for DDGI fixed-time adaptation.");
            return false;
        }
        if (_renderer is not null && !ReferenceEquals(_renderer, renderer))
        {
            SetAvailability(EDDGIGpuTimingAvailability.RendererChanged, "DDGI GPU timing still owns unresolved queries from a different renderer.");
            return false;
        }

        _renderer = renderer;
        int slot = FindAvailablePair();
        if (slot < 0)
        {
            SetAvailability(EDDGIGpuTimingAvailability.Saturated, "DDGI GPU timing query ring is saturated; unresolved GPU queries will not be overwritten.");
            return false;
        }

        ref QueryPair pair = ref _pairs[slot];
        if (!capability.EnsureQueryGenerated(pair.Start) || !capability.EnsureQueryGenerated(pair.End))
        {
            SetAvailability(EDDGIGpuTimingAvailability.Unsupported, "The active renderer could not allocate DDGI GPU timestamp queries.");
            return false;
        }
        if (capability.WriteTimestamp(pair.Start) != ERenderQueryReadStatus.Ready)
        {
            SetAvailability(EDDGIGpuTimingAvailability.Unsupported, "The active renderer rejected the DDGI GPU start timestamp.");
            return false;
        }

        pair.Reset(scheduledProbes);
        pair.StartWritten = true;
        pair.State = EPairState.Open;
        _openSlot = slot;
        SetAvailability(EDDGIGpuTimingAvailability.Pending, null);
        return true;
    }

    /// <summary>
    /// Ends the currently open timestamp pair. Failure quarantines the pair until its start query is terminal.
    /// </summary>
    public void End()
    {
        if (_disposed || _openSlot < 0)
            return;

        int slot = _openSlot;
        _openSlot = -1;
        ref QueryPair pair = ref _pairs[slot];
        if (!TryGetCapability(out IRuntimeRendererHost? renderer, out IOcclusionQueryBackendCapability? capability) ||
            !ReferenceEquals(renderer, _renderer))
        {
            pair.EndAbandoned = true;
            pair.State = EPairState.Pending;
            SetAvailability(EDDGIGpuTimingAvailability.RendererChanged, "DDGI GPU timing end timestamp could not be recorded because the active renderer changed.");
            return;
        }

        if (capability.WriteTimestamp(pair.End) == ERenderQueryReadStatus.Ready)
        {
            pair.EndWritten = true;
            pair.State = EPairState.Pending;
            SetAvailability(EDDGIGpuTimingAvailability.Pending, null);
            return;
        }

        pair.EndAbandoned = true;
        pair.State = EPairState.Pending;
        SetAvailability(EDDGIGpuTimingAvailability.Unsupported, "The active renderer rejected the DDGI GPU end timestamp.");
    }

    /// <summary>Closes interrupted work without accepting its partial duration as a full update measurement.</summary>
    public void Cancel()
    {
        if (_openSlot < 0)
            return;
        _pairs[_openSlot].ScheduledProbes = 0;
        End();
    }

    /// <summary>
    /// Polls timestamp pairs without waiting. Returns one completed milliseconds-per-probe measurement when available.
    /// </summary>
    public bool Resolve(out float millisecondsPerProbe)
    {
        millisecondsPerProbe = 0.0f;
        if (_disposed || _renderer is null)
            return false;
        if (!TryGetCapability(out IRuntimeRendererHost? renderer, out IOcclusionQueryBackendCapability? capability) ||
            !ReferenceEquals(renderer, _renderer))
        {
            SetAvailability(EDDGIGpuTimingAvailability.RendererChanged, "DDGI GPU timing can only resolve queries on their owning renderer.");
            return false;
        }

        for (int slot = 0; slot < _pairs.Length; slot++)
        {
            ref QueryPair pair = ref _pairs[slot];
            if (pair.State is not (EPairState.Open or EPairState.Pending or EPairState.Quarantined))
                continue;

            ResolveQuery(ref pair, pair.Start, isStart: true, capability);
            if (pair.EndWritten)
                ResolveQuery(ref pair, pair.End, isStart: false, capability);

            if (!pair.IsTerminal)
                continue;

            if (pair.StartReady && pair.EndReady)
            {
                ulong nanoseconds = capability.GetElapsedTimestampNanoseconds(pair.StartTicks, pair.EndTicks);
                float candidate = (float)(nanoseconds / 1_000_000.0 / pair.ScheduledProbes);
                if (float.IsFinite(candidate) && candidate >= 0.0f)
                {
                    millisecondsPerProbe = candidate;
                    pair.Reset();
                    SetAvailability(EDDGIGpuTimingAvailability.Ready, null);
                    return true;
                }

                SetAvailability(EDDGIGpuTimingAvailability.InvalidResult, "DDGI GPU timing produced a non-finite milliseconds-per-probe result.");
            }
            else
            {
                SetAvailability(EDDGIGpuTimingAvailability.Unsupported, "DDGI GPU timestamp query was abandoned or could not be read.");
            }

            pair.Reset();
        }

        if (HasLivePairs())
            SetAvailability(EDDGIGpuTimingAvailability.Pending, null);
        return false;
    }

    /// <summary>Releases queries on an active owner or abandons retired-owner handles, then resets the ring.</summary>
    public void Clear()
    {
        bool retiredOwner = _renderer is AbstractRenderer { AcceptsBackendWork: false };
        if (_renderer is not null && !retiredOwner && !ReferenceEquals(AbstractRenderer.Current, _renderer))
        {
            SetAvailability(EDDGIGpuTimingAvailability.RendererChanged, "DDGI GPU timing cannot clear unresolved queries from a renderer that is no longer active.");
            return;
        }

        if (_renderer is not null)
        {
            for (int i = 0; i < _pairs.Length; i++)
            {
                if (!retiredOwner)
                {
                    _pairs[i].Start.Destroy();
                    _pairs[i].End.Destroy();
                }
                // Native teardown owns retired queries. New logical handles are
                // needed at this cold lifecycle boundary, including after Destroy.
                _pairs[i] = new QueryPair();
            }
        }

        for (int i = 0; i < _pairs.Length; i++)
            _pairs[i].Reset();
        _openSlot = -1;
        _renderer = null;
        SetAvailability(EDDGIGpuTimingAvailability.Unrequested, null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Clear();
        _disposed = true;
    }

    private void ResolveQuery(ref QueryPair pair, XRRenderQuery query, bool isStart, IOcclusionQueryBackendCapability capability)
    {
        if (isStart ? pair.StartTerminal : pair.EndTerminal)
            return;

        if (capability.TryConsumeAbandonedTimestamp(query))
        {
            if (isStart) pair.StartAbandoned = true;
            else pair.EndAbandoned = true;
            return;
        }

        ERenderQueryReadStatus status = capability.TryGetTimestamp(query, out TimestampQueryResult result);
        if (status == ERenderQueryReadStatus.Ready)
        {
            if (isStart)
            {
                pair.StartReady = true;
                pair.StartTicks = result.RawTicks;
            }
            else
            {
                pair.EndReady = true;
                pair.EndTicks = result.RawTicks;
            }
            return;
        }
        if (status == ERenderQueryReadStatus.NotReady)
            return;

        pair.State = EPairState.Quarantined;
        SetAvailability(EDDGIGpuTimingAvailability.Unsupported, $"DDGI GPU timestamp query returned {status}; the pair remains quarantined until terminal.");
    }

    private bool TryGetCapability([NotNullWhen(true)] out IRuntimeRendererHost? renderer, [NotNullWhen(true)] out IOcclusionQueryBackendCapability? capability)
    {
        renderer = AbstractRenderer.Current as IRuntimeRendererHost;
        capability = null;
        return renderer is not null &&
            renderer.TryGetBackendCapability<IOcclusionQueryBackendCapability>(out capability) &&
            capability is not null;
    }

    private int FindAvailablePair()
    {
        for (int i = 0; i < _pairs.Length; i++)
            if (_pairs[i].State == EPairState.Available)
                return i;
        return -1;
    }

    private bool HasLivePairs()
    {
        for (int i = 0; i < _pairs.Length; i++)
            if (_pairs[i].State != EPairState.Available)
                return true;
        return false;
    }

    private void SetAvailability(EDDGIGpuTimingAvailability availability, string? diagnostic)
    {
        Availability = availability;
        Diagnostic = diagnostic;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DDGIGpuTiming));
    }

    private enum EPairState : byte
    {
        Available,
        Open,
        Pending,
        Quarantined,
    }

    private struct QueryPair
    {
        public QueryPair() { }
        public readonly XRRenderQuery Start = new(RenderQueryDescriptor.Timestamp);
        public readonly XRRenderQuery End = new(RenderQueryDescriptor.Timestamp);
        public EPairState State;
        public int ScheduledProbes;
        public ulong StartTicks;
        public ulong EndTicks;
        public bool StartWritten;
        public bool EndWritten;
        public bool StartReady;
        public bool EndReady;
        public bool StartAbandoned;
        public bool EndAbandoned;

        public readonly bool StartTerminal => StartReady || StartAbandoned;
        public readonly bool EndTerminal => EndReady || EndAbandoned;
        public readonly bool IsTerminal => StartTerminal && EndTerminal;

        public void Reset(int scheduledProbes = 0)
        {
            State = EPairState.Available;
            ScheduledProbes = scheduledProbes;
            StartTicks = 0;
            EndTicks = 0;
            StartWritten = false;
            EndWritten = false;
            StartReady = false;
            EndReady = false;
            StartAbandoned = false;
            EndAbandoned = false;
        }
    }
}
