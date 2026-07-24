using System.Text;

namespace XREngine.Rendering;

/// <summary>
/// Thread-safe renderer module catalog. Lookups do not allocate and registration is expected
/// to occur only at composition or editor reload boundaries.
/// </summary>
public sealed class RendererBackendCatalog : IRendererBackendCatalog, IDisposable
{
    /// <summary>
    /// Read-only catalog used by hosts that have not installed renderer backend modules.
    /// Required lookups fail with the same actionable diagnostics as an empty catalog.
    /// </summary>
    public static IRendererBackendCatalog Unconfigured { get; } = new UnconfiguredRendererBackendCatalog();

    private readonly object _sync = new();
    private readonly Dictionary<RendererBackendId, RendererBackendCatalogEntry> _entries = [];
    private bool _disposed;

    public int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    public IDisposable Register(
        RendererBackendRegistration registration,
        RendererBackendRegistrationBehavior behavior = RendererBackendRegistrationBehavior.RejectDuplicate)
    {
        ArgumentNullException.ThrowIfNull(registration);

        RendererBackendCatalogEntry newEntry = new(registration);
        RendererBackendCatalogEntry? replacedEntry;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            RendererBackendId id = registration.Metadata.Id;
            if (_entries.TryGetValue(id, out replacedEntry) &&
                behavior == RendererBackendRegistrationBehavior.RejectDuplicate)
            {
                throw new InvalidOperationException(
                    $"Renderer backend module '{id}' is already registered as " +
                    $"'{replacedEntry.Registration.Metadata.DisplayName}'. " +
                    $"Use {nameof(RendererBackendRegistrationBehavior)}.{nameof(RendererBackendRegistrationBehavior.ReplaceExisting)} " +
                    "only after active renderers have been quiesced.");
            }

            replacedEntry?.Registration.Lifecycle?.OnUnregistered();
            try
            {
                registration.Lifecycle?.OnRegistered();
            }
            catch
            {
                replacedEntry?.Registration.Lifecycle?.OnRegistered();
                throw;
            }

            _entries[id] = newEntry;
        }

        return new RendererBackendRegistrationLease(this, registration.Metadata.Id, newEntry);
    }

    public IDisposable Register(
        IRendererBackendModule module,
        RendererBackendRegistrationBehavior behavior = RendererBackendRegistrationBehavior.RejectDuplicate)
        => Register(new RendererBackendRegistration(module), behavior);

    public bool TryGet(RendererBackendId id, out RendererBackendRegistration registration)
    {
        lock (_sync)
        {
            if (!_disposed && _entries.TryGetValue(id, out RendererBackendCatalogEntry? entry))
            {
                registration = entry.Registration;
                return true;
            }
        }

        registration = null!;
        return false;
    }

    public RendererBackendRegistration GetRequired(
        RendererBackendId id,
        RendererBackendCapabilities requiredCapabilities = RendererBackendCapabilities.None)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_entries.TryGetValue(id, out RendererBackendCatalogEntry? entry))
                throw new InvalidOperationException(BuildMissingBackendMessage(id));

            RendererBackendMetadata metadata = entry.Registration.Metadata;
            RendererBackendCapabilities missing = requiredCapabilities & ~metadata.Capabilities;
            if (missing != RendererBackendCapabilities.None)
            {
                throw new InvalidOperationException(
                    $"Renderer backend module '{id}' is installed but does not provide required capabilities '{missing}'. " +
                    $"Installed capabilities: '{metadata.Capabilities}'. Select a compatible backend or install a module that provides the missing capabilities.");
            }

            return entry.Registration;
        }
    }

    public IRuntimeRendererHost CreateRequired(
        RuntimeGraphicsApiKind graphicsApi,
        in RendererBackendCreateContext context,
        RendererBackendCapabilities requiredCapabilities = RendererBackendCapabilities.None)
    {
        RendererBackendId id = RendererBackendId.FromGraphicsApi(graphicsApi);
        RendererBackendRegistration registration = GetRequired(id, requiredCapabilities);
        IRuntimeRendererHost renderer = registration.Factory.Create(context);
        return renderer ?? throw new InvalidOperationException(
            $"Renderer backend factory '{registration.Metadata.DisplayName}' returned null for backend '{id}'.");
    }

    public void Dispose()
    {
        RendererBackendCatalogEntry[] entries;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            entries = [.. _entries.Values];
            _entries.Clear();
        }

        List<Exception>? failures = null;
        for (int i = entries.Length - 1; i >= 0; i--)
        {
            try
            {
                entries[i].Registration.Lifecycle?.OnUnregistered();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more renderer backend modules failed to tear down.", failures);
    }

    internal void Unregister(RendererBackendId id, RendererBackendCatalogEntry expectedEntry)
    {
        bool removed;
        lock (_sync)
        {
            removed = !_disposed &&
                _entries.TryGetValue(id, out RendererBackendCatalogEntry? currentEntry) &&
                ReferenceEquals(currentEntry, expectedEntry) &&
                _entries.Remove(id);
        }

        if (removed)
            expectedEntry.Registration.Lifecycle?.OnUnregistered();
    }

    private string BuildMissingBackendMessage(RendererBackendId requestedId)
    {
        StringBuilder installed = new();
        foreach (RendererBackendId installedId in _entries.Keys)
        {
            if (installed.Length > 0)
                installed.Append(", ");
            installed.Append(installedId.Value);
        }

        string installedDescription = installed.Length == 0 ? "none" : installed.ToString();
        return $"Required renderer backend module '{requestedId}' is not installed. " +
            $"Installed backend modules: {installedDescription}. " +
            "Register the backend at the application composition root before creating a render window.";
    }

}
