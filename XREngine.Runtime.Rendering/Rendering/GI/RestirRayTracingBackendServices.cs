namespace XREngine.Rendering.GI;

/// <summary>Registers the ray tracing bridge with the lifetime of its renderer module.</summary>
public static class RestirRayTracingBackendServices
{
    private static IRestirRayTracingBackend? _current;

    public static IRestirRayTracingBackend? Current => Volatile.Read(ref _current);

    public static IDisposable Register(IRestirRayTracingBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        Interlocked.Exchange(ref _current, backend);
        return new Registration(backend);
    }

    private sealed class Registration(IRestirRayTracingBackend backend) : IDisposable
    {
        private IRestirRayTracingBackend? _backend = backend;

        public void Dispose()
        {
            IRestirRayTracingBackend? current = Interlocked.Exchange(ref _backend, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, null, current);
        }
    }
}
