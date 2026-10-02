namespace XREngine.Rendering;

/// <summary>
/// Fixed-storage contact stream. Overflow discards the whole gesture generation rather than
/// losing a release and leaving a control held. A new begin is required after cancellation.
/// </summary>
public sealed class WindowPointerContactBuffer
{
    public const int MaximumContacts = 10;
    public const int Capacity = 128;
    private readonly object _sync = new();
    private readonly WindowPointerContact[] _events = new WindowPointerContact[Capacity];
    private readonly int[] _activeIds = new int[MaximumContacts];
    private int _activeCount;
    private int _count;
    private ulong _generation = 1;
    private ulong _sequence;

    /// <summary>Watermark for rejecting input that predates a controller's ownership, without draining a shared source.</summary>
    public ulong LatestSequence
    {
        get { lock (_sync) return _sequence; }
    }

    public ulong Generation
    {
        get { lock (_sync) return _generation; }
    }

    public bool Record(WindowPointerContact contact)
    {
        if (contact.Id < 0 || (uint)contact.Phase > (uint)EPointerContactPhase.Cancelled ||
            !float.IsFinite(contact.X) || !float.IsFinite(contact.Y))
            return false;

        lock (_sync)
        {
            int active = Array.IndexOf(_activeIds, contact.Id, 0, _activeCount);
            if (contact.Phase == EPointerContactPhase.Began)
            {
                if (active >= 0 || _activeCount == MaximumContacts)
                    return false;
            }
            else if (active < 0)
                return false;

            // Only adjacent moves from the same contact may coalesce. Begin/end ordering survives.
            if (contact.Phase == EPointerContactPhase.Moved && _count > 0 &&
                _events[_count - 1] is { Phase: EPointerContactPhase.Moved } previous && previous.Id == contact.Id)
            {
                _events[_count - 1] = contact with { Sequence = ++_sequence };
                return true;
            }
            if (_count == Capacity)
            {
                CancelCore();
                return false;
            }
            if (contact.Phase == EPointerContactPhase.Began)
                _activeIds[_activeCount++] = contact.Id;
            else if (contact.Phase is EPointerContactPhase.Ended or EPointerContactPhase.Cancelled)
                _activeIds[active] = _activeIds[--_activeCount];
            _events[_count++] = contact with { Sequence = ++_sequence };
            return true;
        }
    }

    public int Consume(Span<WindowPointerContact> destination, out ulong generation)
    {
        if (destination.Length < Capacity)
            throw new ArgumentException("Contact storage must hold the complete bounded stream.", nameof(destination));
        lock (_sync)
        {
            generation = _generation;
            int count = _count;
            _events.AsSpan(0, count).CopyTo(destination);
            _count = 0;
            return count;
        }
    }

    public void Cancel()
    {
        lock (_sync)
            CancelCore();
    }

    private void CancelCore()
    {
        _count = 0;
        _activeCount = 0;
        ++_generation;
    }
}
