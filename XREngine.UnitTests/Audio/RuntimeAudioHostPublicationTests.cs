using NUnit.Framework;
using XREngine;
using XREngine.Data;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Audio;

[TestFixture, NonParallelizable]
public sealed class RuntimeAudioHostPublicationTests
{
    [Test]
    public void BootstrapUninstallRejectionKeepsLeaseForRetry()
    {
        IRuntimeAudioIntegrationServices previous = RuntimeAudioIntegrationServices.Current;
        IDisposable lease = RuntimeAdapterBootstrap.InstallEngineHostServices(RuntimeAdapterProfile.Audio);
        try
        {
            IRuntimeAudioIntegrationServices installed = RuntimeAudioIntegrationServices.Current;
            using (RuntimeSourcePublicationServices.BeginPublication(RuntimeSourcePublicationServices.Generation))
            {
                Assert.Throws<InvalidOperationException>(RuntimeAdapterBootstrap.UninstallEngineHostServices);
                Assert.That(RuntimeAudioIntegrationServices.Current, Is.SameAs(installed));
            }

            RuntimeAdapterBootstrap.UninstallEngineHostServices();
            Assert.That(RuntimeAudioIntegrationServices.Current, Is.SameAs(previous));
        }
        finally
        {
            lease.Dispose();
            RuntimeAdapterBootstrap.UninstallEngineHostServices();
        }
    }

    [Test]
    public void BootstrapLeaseDisposeRejectionCanRetry()
    {
        IRuntimeAudioIntegrationServices previous = RuntimeAudioIntegrationServices.Current;
        IDisposable lease = RuntimeAdapterBootstrap.InstallEngineHostServices(RuntimeAdapterProfile.Audio);
        try
        {
            IRuntimeAudioIntegrationServices installed = RuntimeAudioIntegrationServices.Current;
            using (RuntimeSourcePublicationServices.BeginPublication(RuntimeSourcePublicationServices.Generation))
            {
                Assert.Throws<InvalidOperationException>(lease.Dispose);
                Assert.That(RuntimeAudioIntegrationServices.Current, Is.SameAs(installed));
            }

            lease.Dispose();
            Assert.That(RuntimeAudioIntegrationServices.Current, Is.SameAs(previous));
        }
        finally
        {
            lease.Dispose();
            RuntimeAdapterBootstrap.UninstallEngineHostServices();
        }
    }

    [Test]
    public void BootstrapReplacementRejectionKeepsOriginalLeaseForRetry()
    {
        IRuntimeAudioIntegrationServices previous = RuntimeAudioIntegrationServices.Current;
        IDisposable first = RuntimeAdapterBootstrap.InstallEngineHostServices(RuntimeAdapterProfile.Audio);
        IDisposable? second = null;
        try
        {
            IRuntimeAudioIntegrationServices installed = RuntimeAudioIntegrationServices.Current;
            using (RuntimeSourcePublicationServices.BeginPublication(RuntimeSourcePublicationServices.Generation))
            {
                Assert.Throws<InvalidOperationException>(() => RuntimeAdapterBootstrap.InstallEngineHostServices(RuntimeAdapterProfile.Audio));
                Assert.That(RuntimeAudioIntegrationServices.Current, Is.SameAs(installed));
            }

            second = RuntimeAdapterBootstrap.InstallEngineHostServices(RuntimeAdapterProfile.Audio);
            Assert.That(RuntimeAudioIntegrationServices.Current, Is.Not.SameAs(installed));
            second.Dispose();
            Assert.That(RuntimeAudioIntegrationServices.Current, Is.SameAs(previous));
        }
        finally
        {
            second?.Dispose();
            first.Dispose();
            RuntimeAdapterBootstrap.UninstallEngineHostServices();
        }
    }

    [Test]
    public void RenderingLeaseDisposeRejectionKeepsAdaptersForRetry()
    {
        IRuntimeAudioIntegrationServices previous = RuntimeAudioIntegrationServices.Current;
        var profile = new RuntimeApplicationProfile("AudioTest", RuntimeAdapterProfile.Audio,
            AllowsWindows: false, AllowsVr: false, RegisterRendererBackends: false);
        IDisposable lease = RuntimeRenderingBootstrap.InstallEngineHostServices(profile);
        try
        {
            IRuntimeAudioIntegrationServices installed = RuntimeAudioIntegrationServices.Current;
            using (RuntimeSourcePublicationServices.BeginPublication(RuntimeSourcePublicationServices.Generation))
            {
                Assert.Throws<InvalidOperationException>(() => RuntimeRenderingBootstrap.InstallEngineHostServices(profile));
                Assert.Throws<InvalidOperationException>(lease.Dispose);
                Assert.That(RuntimeAudioIntegrationServices.Current, Is.SameAs(installed));
            }

            lease.Dispose();
            Assert.That(RuntimeAudioIntegrationServices.Current, Is.SameAs(previous));
        }
        finally
        {
            lease.Dispose();
            RuntimeAdapterBootstrap.UninstallEngineHostServices();
        }
    }
}
