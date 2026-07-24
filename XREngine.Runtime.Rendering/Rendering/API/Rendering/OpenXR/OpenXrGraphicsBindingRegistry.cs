using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Process-wide, explicitly populated registry for OpenXR graphics bindings.
/// This registry deliberately avoids assembly scanning so registration remains native-AOT safe.
/// </summary>
public static class OpenXrGraphicsBindingRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<RendererBackendId, RegistrationEntry> Registrations = [];

    /// <summary>
    /// Registers a binding factory for a concrete renderer backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when another module already owns the same backend identifier.
    /// </exception>
    public static IDisposable Register(
        RendererBackendId backendId,
        Func<IXrGraphicsBinding> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (Sync)
        {
            if (Registrations.TryGetValue(backendId, out RegistrationEntry? existing))
            {
                if (!existing.Factory.Equals(factory))
                {
                    throw new InvalidOperationException(
                        $"Another OpenXR graphics module already owns backend '{backendId}'.");
                }

                existing.LeaseCount++;
                return new RegistrationLease(backendId, factory);
            }

            Registrations.Add(backendId, new RegistrationEntry(factory));
        }

        return new RegistrationLease(backendId, factory);
    }

    /// <summary>
    /// Creates the registered binding for <paramref name="renderer"/>.
    /// </summary>
    public static bool TryCreate(
        AbstractRenderer renderer,
        [NotNullWhen(true)] out IXrGraphicsBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(renderer);

        Func<IXrGraphicsBinding>? factory;
        lock (Sync)
            factory = Registrations.TryGetValue(renderer.BackendId, out RegistrationEntry? registration)
                ? registration.Factory
                : null;

        binding = factory?.Invoke();
        if (binding is null)
            return false;

        if (binding.BackendId == renderer.BackendId && binding.IsCompatible(renderer))
            return true;

        binding = null;
        return false;
    }

    private sealed class RegistrationLease(
        RendererBackendId backendId,
        Func<IXrGraphicsBinding> factory) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            lock (Sync)
            {
                if (Registrations.TryGetValue(backendId, out RegistrationEntry? current) &&
                    current.Factory.Equals(factory))
                {
                    current.LeaseCount--;
                    if (current.LeaseCount == 0)
                        Registrations.Remove(backendId);
                }
            }

            _disposed = true;
        }
    }

    private sealed class RegistrationEntry(Func<IXrGraphicsBinding> factory)
    {
        public Func<IXrGraphicsBinding> Factory { get; } = factory;
        public int LeaseCount { get; set; } = 1;
    }
}
