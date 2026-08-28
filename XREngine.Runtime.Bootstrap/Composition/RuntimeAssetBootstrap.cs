using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Serialization;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Composes reversible asset registrations owned by the runtime modules.</summary>
public static class RuntimeAssetBootstrap
{
    private static readonly object Sync = new();
    private static IDisposable? _installation;
    private static int _referenceCount;

    /// <summary>Installs engine-wide asset services and returns their reverse-order aggregate lease.</summary>
    public static IDisposable InstallEngineAssetServices()
    {
        lock (Sync)
        {
            _installation ??= CreateInstallation();
            checked
            {
                _referenceCount++;
            }
            return new SharedInstallationLease();
        }
    }

    private static IDisposable CreateInstallation()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(DataAssetSerializationRegistration.Install());
            leases.Add(RuntimeCoreAssetSerializationRegistration.Install(
                new AssetManagerAssetSerializationServices(Engine.Assets)));
            leases.Add(AnimationSerializationRegistration.Install());
            leases.Add(RenderingSerializationRegistration.Install());
            leases.Add(BootstrapAssetSerializationRegistration.Install());
        });

    private sealed class SharedInstallationLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (Sync)
            {
                if (_referenceCount <= 0)
                    throw new InvalidOperationException("Runtime asset-service reference count underflowed.");

                _referenceCount--;
                if (_referenceCount != 0)
                    return;

                IDisposable? installation = _installation;
                _installation = null;
                installation?.Dispose();
            }
        }
    }
}
