using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>Retains destination search intent through activation and the first host save.</summary>
internal sealed class NativeAnimationSearchReceiveReceipt : IDisposable
{
    private readonly AnimationPanel _panel;
    private NativeAnimationSearchFieldReceipt _field;
    private readonly string _query;
    private bool _failed, _disposed, _rollbackActivation;
    private string? _resetQuery;

    internal NativeAnimationSearchReceiveReceipt(AnimationPanel panel, NativeAnimationSearchFieldReceipt field)
    {
        _panel = panel;
        _field = field;
        _query = panel.CommittedPathSearch;
    }

    internal bool IsCurrent => !_failed && !_disposed &&
        ReferenceEquals(_panel.SearchReceiveReceipt, this) &&
        _panel.CommittedPathSearch == _query &&
        (_resetQuery is { } reset ? _field.IsGuardedTextWriteOwned(reset) : _field.IsCurrent(0, _query));

    internal void RequireCurrent()
    {
        if (IsCurrent) return;
        _failed = true;
        throw new InvalidOperationException("The destination Animation search intent changed.");
    }

    internal void WriteActivationDisplay(UITextInputComponent input, UITextComponent display, string value)
    {
        if (_rollbackActivation && ReferenceEquals(input, _field.Input) && _field.OwnsDisplay(display)) return;
        try { _field.WriteActivationDisplay(display, value, RequireCurrent); }
        catch { _failed = true; throw; }
    }

    internal void ResetDraft(UITextInputComponent input, string query, Action<Action> write)
    {
        if (_rollbackActivation && ReferenceEquals(input, _field.Input)) return;
        RequireCurrent();
        _resetQuery = query;
        try
        {
            write(RequireCurrent);
            RequireCurrent();
            if (!_field.IsCurrent(2, query) ||
                !NativeAnimationSearchFieldReceipt.TryCapture(_panel, _field.Input, out var next))
                throw new InvalidOperationException("The destination search reset changed.");
            _field = next;
            _resetQuery = null;
        }
        catch { _failed = true; throw; }
    }

    internal void RestoreStructure(Action restore)
    {
        var previous = _panel.SearchReceiveReceipt;
        if (previous is not null && !ReferenceEquals(previous, this))
        {
            restore();
            return;
        }
        bool previousMode = _rollbackActivation;
        _rollbackActivation = true;
        _panel.SearchReceiveReceipt = this;
        try { restore(); }
        finally
        {
            _rollbackActivation = previousMode;
            if (ReferenceEquals(_panel.SearchReceiveReceipt, this))
                _panel.SearchReceiveReceipt = previous;
        }
    }

    internal void Complete()
    {
        RequireCurrent();
        Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        if (ReferenceEquals(_panel.SearchReceiveReceipt, this))
            _panel.SearchReceiveReceipt = null;
    }
}
