using XREngine.Data;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Installs one explicit application profile and restores it deterministically.</summary>
public static class RuntimeApplicationBootstrap
{
    private static readonly object Sync = new();
    private static ApplicationInstallation? _current;

    public static RuntimeApplicationProfile? CurrentProfile
    {
        get
        {
            lock (Sync)
                return _current?.Profile;
        }
    }

    public static IDisposable Install(RuntimeApplicationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Validate(profile);

        lock (Sync)
        {
            ApplicationInstallation? previous = _current;
            _current = null;
            previous?.DisposeWithoutLock();

            ApplicationInstallation installation = new(profile);
            _current = installation;
            return installation;
        }
    }

    public static void Uninstall()
    {
        lock (Sync)
        {
            ApplicationInstallation? current = _current;
            _current = null;
            current?.DisposeWithoutLock();
        }
    }

    private static void Validate(RuntimeApplicationProfile profile)
    {
        if (profile.AllowsVr && !profile.AllowsLocalInput)
            throw new InvalidOperationException($"Application profile '{profile.Name}' enables VR without the input adapter.");
        if (profile.RegisterRendererBackends && !profile.AllowsWindows)
            throw new InvalidOperationException($"Application profile '{profile.Name}' registers desktop renderer backends without window permission.");
    }

    private sealed class ApplicationInstallation : IDisposable
    {
        private readonly IDisposable _capabilityLease;
        private readonly IDisposable _servicesLease;
        private int _disposed;

        public ApplicationInstallation(RuntimeApplicationProfile profile)
        {
            Profile = profile;
            _capabilityLease = RuntimeApplicationCapabilityServices.Install(profile.ToCapabilities());
            try
            {
                _servicesLease = profile.AllowsWindows
                    ? RuntimeRenderingBootstrap.InstallEngineHostServices(profile)
                    : InstallHeadlessServices(profile);
            }
            catch
            {
                _capabilityLease.Dispose();
                throw;
            }
        }

        public RuntimeApplicationProfile Profile { get; }

        private static IDisposable InstallHeadlessServices(RuntimeApplicationProfile profile)
            => RegistrationLeaseGroup.Create(leases =>
            {
                // Published worlds require the same serialization/cooked-asset roots as
                // windowed hosts even though a headless process installs no renderer.
                leases.Add(RuntimeAssetBootstrap.InstallEngineAssetServices());
                leases.Add(RuntimeAdapterBootstrap.InstallEngineHostServices(profile.AdapterProfile));
            });

        public void Dispose()
        {
            lock (Sync)
            {
                if (ReferenceEquals(_current, this))
                    _current = null;
                DisposeWithoutLock();
            }
        }

        public void DisposeWithoutLock()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                _servicesLease.Dispose();
            }
            finally
            {
                _capabilityLease.Dispose();
            }
        }
    }
}
