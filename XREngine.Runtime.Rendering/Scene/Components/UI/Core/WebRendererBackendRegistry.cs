using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering.UI;

/// <summary>
/// Explicit, native-AOT-safe registry for optional renderer-module-owned web backends.
/// </summary>
public static class WebRendererBackendRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<RendererBackendId, RegistrationEntry> Registrations = [];

    /// <summary>
    /// Registers an accelerated web-renderer factory owned by a renderer module.
    /// The returned lease must be disposed before unloading that module.
    /// </summary>
    public static IDisposable RegisterAccelerated(
        RendererBackendId backendId,
        Func<IWebRendererBackend> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (Sync)
        {
            if (Registrations.TryGetValue(backendId, out RegistrationEntry? existing))
            {
                if (!existing.Factory.Equals(factory))
                {
                    throw new InvalidOperationException(
                        $"Another accelerated web-renderer module already owns backend '{backendId}'.");
                }

                existing.LeaseCount++;
                return new RegistrationLease(backendId, factory);
            }

            Registrations.Add(backendId, new RegistrationEntry(factory));
        }

        return new RegistrationLease(backendId, factory);
    }

    /// <summary>
    /// Tries to create the accelerated web renderer registered for <paramref name="backendId"/>.
    /// </summary>
    public static bool TryCreateAccelerated(
        RendererBackendId backendId,
        [NotNullWhen(true)] out IWebRendererBackend? backend)
    {
        Func<IWebRendererBackend>? factory;
        lock (Sync)
            factory = Registrations.TryGetValue(backendId, out RegistrationEntry? registration)
                ? registration.Factory
                : null;

        backend = factory?.Invoke();
        return backend is not null;
    }

    /// <summary>
    /// Creates the accelerated web renderer for <paramref name="backendId"/>, or fails visibly
    /// when that renderer module does not provide one.
    /// </summary>
    public static IWebRendererBackend CreateRequiredAccelerated(RendererBackendId backendId)
        => TryCreateAccelerated(backendId, out IWebRendererBackend? backend)
            ? backend
            : throw new NotSupportedException(
                $"Renderer backend '{backendId}' does not provide an accelerated web renderer.");

    private sealed class RegistrationLease(
        RendererBackendId backendId,
        Func<IWebRendererBackend> factory) : IDisposable
    {
        private readonly RendererBackendId _backendId = backendId;
        private Func<IWebRendererBackend>? _factory = factory;

        public void Dispose()
        {
            Func<IWebRendererBackend>? factory = Interlocked.Exchange(ref _factory, null);
            if (factory is null)
                return;

            lock (Sync)
            {
                if (!Registrations.TryGetValue(_backendId, out RegistrationEntry? current) ||
                    !current.Factory.Equals(factory))
                    return;

                current.LeaseCount--;
                if (current.LeaseCount == 0)
                    Registrations.Remove(_backendId);
            }
        }
    }

    private sealed class RegistrationEntry(Func<IWebRendererBackend> factory)
    {
        public Func<IWebRendererBackend> Factory { get; } = factory;
        public int LeaseCount { get; set; } = 1;
    }
}
