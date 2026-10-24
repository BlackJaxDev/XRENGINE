namespace XREngine.Rendering.UI;

/// <summary>Matches native indexed draws to one receipt without per-draw storage allocation.</summary>
internal struct UIIndexedDrawScopeState
{
    private UIIndexedDrawIntent _first;
    private UIIndexedDrawIntent _second;
    private UIIndexedDrawIntent _third;
    private ulong _receiptId;
    private long _backendGeneration;
    private int _count;
    private int _acknowledged;
    private bool _failed;

    internal ulong ScopeId { get; private set; }
    internal readonly ulong ReceiptId => _receiptId;

    internal bool TryBegin(ulong scopeId, ulong receiptId, long backendGeneration,
        ReadOnlySpan<UIIndexedDrawIntent> expected)
    {
        if (ScopeId != 0UL)
        {
            _failed = true;
            return false;
        }
        if (scopeId == 0UL || receiptId == 0UL || expected.Length is < 1 or > 3)
            return false;
        for (int index = 0; index < expected.Length; index++)
        {
            UIIndexedDrawIntent intent = expected[index];
            if (intent.Renderer is null || intent.IndexCount == 0 || intent.Instances == 0 || intent.IndexOffset < 0)
                return false;
            for (int previous = 0; previous < index; previous++)
                if (Matches(intent, expected[previous]))
                    return false;
        }
        _first = expected[0];
        _second = expected.Length > 1 ? expected[1] : default;
        _third = expected.Length > 2 ? expected[2] : default;
        _receiptId = receiptId;
        _backendGeneration = backendGeneration;
        _count = expected.Length;
        _acknowledged = 0;
        _failed = false;
        ScopeId = scopeId;
        return true;
    }

    internal bool Acknowledge(ulong scopeId, ulong receiptId, long backendGeneration,
        in UIIndexedDrawIntent actual, bool nativeSucceeded)
    {
        if (ScopeId == 0UL)
            return false;
        if (!nativeSucceeded || scopeId != ScopeId || receiptId != _receiptId ||
            backendGeneration != _backendGeneration)
        {
            _failed = true;
            return false;
        }
        for (int index = 0; index < _count; index++)
        {
            UIIndexedDrawIntent expected = index switch { 0 => _first, 1 => _second, _ => _third };
            if (!Matches(actual, expected))
                continue;
            int bit = 1 << index;
            if ((_acknowledged & bit) != 0)
            {
                _failed = true;
                return false;
            }
            _acknowledged |= bit;
            return !_failed;
        }
        _failed = true;
        return false;
    }

    internal bool Complete(ulong scopeId, ulong receiptId, long backendGeneration)
    {
        if (ScopeId == 0UL)
            return false;
        if (scopeId != ScopeId)
        {
            _failed = true;
            return false;
        }
        bool success = !_failed && receiptId == _receiptId && backendGeneration == _backendGeneration
            && _acknowledged == (1 << _count) - 1;
        this = default;
        return success;
    }

    private static bool Matches(in UIIndexedDrawIntent left, in UIIndexedDrawIntent right)
        => ReferenceEquals(left.Renderer, right.Renderer) && left.GeometryRevision == right.GeometryRevision
        && left.Topology == right.Topology && left.ElementType == right.ElementType
        && left.IndexCount == right.IndexCount && left.Instances == right.Instances
        && left.IndexOffset == right.IndexOffset;
}
