namespace XREngine.Rendering.UI;

/// <summary>Holds the optional native text shaping service.</summary>
public static class UITextShapingBackendRegistry
{
    private static IUITextShapingBackend? _current;
    private static long _generation;

    public static event Action? Changed;

    public static IUITextShapingBackend? Current
    {
        get => System.Threading.Volatile.Read(ref _current);
        set
        {
            IUITextShapingBackend? old = System.Threading.Interlocked.Exchange(ref _current, value);
            if (ReferenceEquals(old, value))
                return;
            System.Threading.Interlocked.Increment(ref _generation);
            Changed?.Invoke();
        }
    }

    public static long Generation => System.Threading.Volatile.Read(ref _generation);
}
