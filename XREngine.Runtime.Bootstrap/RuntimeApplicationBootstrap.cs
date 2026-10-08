using XREngine.Data;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Installs one explicit application profile and restores it deterministically.</summary>
public static class RuntimeApplicationBootstrap
{
    private static readonly object Sync = new();
    private static ApplicationInstallation? _current;

    /// <summary>Installs packaged desktop providers before reading launch assets to select an application profile.</summary>
    public static void PrepareDesktopServices() => DesktopRuntimeBackendBootstrap.EnsureRegistered();

    /// <summary>Registers native logging before an application reads assets or starts workers.</summary>
    public static void PrepareLoggingServices() => DesktopLoggingBackend.EnsureRegistered();

    /// <summary>Registers native worker services before the first DEBUG Engine access or threaded scheduler setup.</summary>
    public static void PrepareWorkerServices() => DesktopWorkerBackend.EnsureRegistered();

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
        DesktopRuntimeBackendBootstrap.EnsureRegistered();

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
                leases.Add(RuntimeEngineStartupPolicyServices.Install(DesktopEngineStartupPolicy.Instance));
                leases.Add(RuntimeAssetBootstrap.InstallEngineAssetServices());
                leases.Add(EngineRuntimeShaderServices.Install());
                leases.Add(RuntimeAdapterBootstrap.InstallEngineHostServices(
                    profile.AdapterProfile,
                    static () => new Scene.Physics.Jolt.JoltScene(),
                    composeRenderedWorlds: false));
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
