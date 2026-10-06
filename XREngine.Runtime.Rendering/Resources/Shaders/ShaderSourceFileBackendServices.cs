namespace XREngine.Rendering;

/// <summary>Stores the host file backend used by shader source refresh jobs.</summary>
public static class ShaderSourceFileBackendServices
{
    private static readonly object Gate = new();
    private static IShaderSourceFileBackend? _current;
    private static int _generation;

    public static IShaderSourceFileBackend? Current
    {
        get => Volatile.Read(ref _current);
        set
        {
            lock (Gate)
            {
                Volatile.Write(ref _current, value);
                _generation = unchecked(_generation + 1);
            }
        }
    }

    /// <summary>Captures one backend installation for an entire refresh job.</summary>
    internal static bool TryCapture(out IShaderSourceFileBackend? backend, out int generation)
    {
        lock (Gate)
        {
            backend = _current;
            generation = _generation;
            return backend is not null;
        }
    }

    /// <summary>Rejects work from a replaced backend, including a reinstalled instance.</summary>
    internal static bool IsCurrent(IShaderSourceFileBackend backend, int generation)
    {
        lock (Gate)
            return ReferenceEquals(_current, backend) && _generation == generation;
    }
}
