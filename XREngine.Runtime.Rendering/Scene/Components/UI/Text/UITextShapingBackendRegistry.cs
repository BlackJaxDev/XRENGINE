namespace XREngine.Rendering.UI;

/// <summary>Holds the optional native text shaping service.</summary>
public static class UITextShapingBackendRegistry
{
    private static UITextShapingBackendState _state = new(null, 0);

    public static event Action? Changed;

    public static IUITextShapingBackend? Current
    {
        get => System.Threading.Volatile.Read(ref _state).Backend;
        set
        {
            while (true)
            {
                UITextShapingBackendState old = System.Threading.Volatile.Read(ref _state);
                if (ReferenceEquals(old.Backend, value))
                    return;
                UITextShapingBackendState next = new(value, checked(old.Generation + 1));
                if (!ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref _state, next, old), old))
                    continue;
                Changed?.Invoke();
                return;
            }
        }
    }

    public static long Generation => State.Generation;
    internal static UITextShapingBackendState State => System.Threading.Volatile.Read(ref _state);

    /// <summary>Invalidate captured requests when a paragraph analyzer changes.</summary>
    internal static void InvalidateCapturedRequests()
    {
        while (true)
        {
            UITextShapingBackendState old = State;
            UITextShapingBackendState next = new(old.Backend, checked(old.Generation + 1));
            if (!ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref _state, next, old), old))
                continue;
            Changed?.Invoke();
            return;
        }
    }
}
