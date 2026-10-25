using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Tracks only captured local values while a forest's Undo properties replay.</summary>
internal sealed class HierarchyBatchReplayExpectation
{
    private readonly TransformBase[] _transforms;
    private readonly HierarchyBatchLocalValues[] _before;
    private readonly HierarchyBatchLocalValues[] _after;
    private readonly HierarchyBatchLocalValues[] _expected;
    private bool _replaying;
    private bool _undo;
    private bool _pending;

    internal HierarchyBatchReplayExpectation(IReadOnlyList<TransformBase> transforms)
    {
        _transforms = [.. transforms];
        _before = new HierarchyBatchLocalValues[transforms.Count];
        _after = new HierarchyBatchLocalValues[transforms.Count];
        _expected = new HierarchyBatchLocalValues[transforms.Count];
        for (int i = 0; i < transforms.Count; i++)
        {
            _before[i] = HierarchyBatchLocalValues.Capture(transforms[i]);
            _expected[i] = _before[i];
        }
    }

    internal int Count => _transforms.Length;
    internal bool Matches(int index) => _expected[index].Matches(_transforms[index]);

    internal void RequireValues(int except = -1)
    {
        for (int i = 0; i < _transforms.Length; i++)
            if (i != except && !Matches(i))
                throw new InvalidOperationException("A clone local value changed outside this replay.");
    }

    internal void CaptureApplied(int index)
    {
        RequireValues(index);
        _after[index] = HierarchyBatchLocalValues.Capture(_transforms[index]);
        _expected[index] = _after[index];
    }

    internal void RestoreBefore(int index) => _expected[index] = _before[index];
    internal void RestoreAfter(int index) => _expected[index] = _after[index];

    internal void BeginReplay(bool undo)
    {
        if (_replaying || _pending)
            throw new InvalidOperationException("The prior clone replay did not complete.");
        RequireValues();
        _undo = undo;
        _replaying = true;
    }

    internal void BeginProperty(TransformBase target, string name)
    {
        if (!_replaying || _pending)
            throw new InvalidOperationException("A clone property replay is missing or reentered.");
        RequireValues();
        int index = Array.FindIndex(_transforms, transform => ReferenceEquals(transform, target));
        if (index < 0 || !_expected[index].IsLocal || !HierarchyBatchLocalValues.IsKey(name))
            throw new InvalidOperationException("The clone replay does not own this exact local key.");
        _expected[index] = _expected[index].With(name, _undo ? _before[index] : _after[index]);
        _pending = true;
    }

    internal void RequireCurrent()
    {
        if (!_replaying || !_pending)
            throw new InvalidOperationException("No clone property setter is admitted.");
        RequireValues();
    }

    internal void EndProperty() => _pending = false;

    internal void RequireComplete()
    {
        if (!_replaying || _pending)
            throw new InvalidOperationException("The clone property replay did not settle.");
        RequireValues();
        for (int i = 0; i < _transforms.Length; i++)
        {
            HierarchyBatchLocalValues target = _before[i].IsLocal
                ? (_undo ? _before[i] : _after[i])
                : (_undo ? _after[i] : _before[i]);
            if (!_expected[i].SameProperties(target))
                throw new InvalidOperationException("The clone local-key replay is incomplete.");
        }
    }

    internal void EndReplay()
    {
        RequireValues();
        if (_pending) throw new InvalidOperationException("The clone property setter did not finish.");
        _replaying = false;
    }
}
