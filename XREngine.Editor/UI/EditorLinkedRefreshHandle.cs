using System.Reflection;

namespace XREngine.Editor.UI;

/// <summary>Keeps Undo history from retaining a closed inspector view.</summary>
internal sealed class EditorLinkedRefreshHandle
{
    private readonly WeakReference<object>? _target;
    private readonly MethodInfo? _method;
    private readonly Action? _staticAction;

    public EditorLinkedRefreshHandle(Action? refresh)
    {
        if (refresh?.Target is { } target)
        {
            _target = new(target);
            _method = refresh.Method;
        }
        else _staticAction = refresh;
    }

    public void Invoke()
    {
        if (_target is null) _staticAction?.Invoke();
        else if (_target.TryGetTarget(out object? target)) _method!.Invoke(target, null);
    }
}
