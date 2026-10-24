using NUnit.Framework;
using Shouldly;
using XREngine.Audio;
using XREngine.Components;

namespace XREngine.UnitTests.Audio;

[TestFixture, NonParallelizable]
public sealed class AudioSourceTeardownTests
{
    private const EAudioTransport TestTransport = (EAudioTransport)int.MaxValue;

    [Test]
    public void DirectDestroyReleasesTheListenersNativeSource()
    {
        WithFakeAudioManager(manager =>
        {
            using ListenerContext listener = manager.NewListener();
            FakeAudioTeardownTransport transport = (FakeAudioTeardownTransport)listener.Transport!;
            AudioSourceComponent component = new();
            component.AddSourceToListener(listener);
            transport.CreatedSources.ShouldBe(1);
            listener.Sources.Count.ShouldBe(1);

            component.Destroy(true);

            component.ActiveListenerCount.ShouldBe(0);
            listener.Sources.Count.ShouldBe(0);
            transport.DestroyedSources.ShouldBe(1);
        });
    }

    [Test]
    public void NativeRetirementCallbackCannotReenterDestroyedComponent()
    {
        WithFakeAudioManager(manager =>
        {
            using ListenerContext listener = manager.NewListener();
            FakeAudioTeardownTransport transport = (FakeAudioTeardownTransport)listener.Transport!;
            AudioSourceComponent component = new();
            component.AddSourceToListener(listener);
            transport.OnDestroySource = _ => component.AddSourceToListener(listener);

            component.Destroy(true);

            component.ActiveListenerCount.ShouldBe(0);
            transport.CreatedSources.ShouldBe(1);
            transport.DestroyedSources.ShouldBe(1);
        });
    }

    [Test]
    public void OneFailedNativeReleaseDoesNotSkipOtherListener()
    {
        WithFakeAudioManager(manager =>
        {
            using ListenerContext first = manager.NewListener();
            using ListenerContext second = manager.NewListener();
            FakeAudioTeardownTransport firstTransport = (FakeAudioTeardownTransport)first.Transport!;
            FakeAudioTeardownTransport secondTransport = (FakeAudioTeardownTransport)second.Transport!;
            AudioSourceComponent component = new();
            component.AddSourceToListener(first);
            component.AddSourceToListener(second);
            int quarantinedBefore = AudioSourceComponent.UncertainSourceRetirementCount;
            firstTransport.OnDestroySource = _ => throw new InvalidOperationException("Expected release failure.");

            component.Destroy(true);

            component.ActiveListenerCount.ShouldBe(0);
            firstTransport.DestroyedSources.ShouldBe(1);
            secondTransport.DestroyedSources.ShouldBe(1);
            AudioSourceComponent.UncertainSourceRetirementCount.ShouldBe(quarantinedBefore + 1);
        });
    }

    [Test]
    public void CreateCallbackLifecycleChangeRejectsTheOldCandidate()
    {
        WithFakeAudioManager(manager =>
        {
            using ListenerContext listener = manager.NewListener();
            FakeAudioTeardownTransport transport = (FakeAudioTeardownTransport)listener.Transport!;
            AudioSourceLifecycleProbe component = new();
            transport.OnCreateSource = _ =>
            {
                transport.OnCreateSource = null;
                component.DeactivateForTest();
                component.ActivateForTest();
            };

            component.AddSourceToListener(listener);
            component.ActiveListenerCount.ShouldBe(0);
            transport.CreatedSources.ShouldBe(1);
            transport.DestroyedSources.ShouldBe(1);

            component.AddSourceToListener(listener);
            component.ActiveListenerCount.ShouldBe(1);
            component.Destroy(true);
        });
    }

    [Test]
    public void ActivationDuringRetirementKeepsUpdateTickAndRejectsReentrantSource()
    {
        WithFakeAudioManager(manager =>
        {
            using ListenerContext listener = manager.NewListener();
            FakeAudioTeardownTransport transport = (FakeAudioTeardownTransport)listener.Transport!;
            AudioSourceLifecycleProbe component = new();
            component.ActivateForTest();
            component.HasPositionUpdateTick.ShouldBeTrue();
            component.AddSourceToListener(listener);
            transport.OnDestroySource = _ =>
            {
                transport.OnDestroySource = null;
                component.ActivateForTest();
                component.AddSourceToListener(listener);
            };

            component.DeactivateForTest();
            component.ActiveListenerCount.ShouldBe(0);
            transport.CreatedSources.ShouldBe(1);
            transport.DestroyedSources.ShouldBe(1);
            component.HasPositionUpdateTick.ShouldBeTrue();

            component.AddSourceToListener(listener);
            component.ActiveListenerCount.ShouldBe(1);
            component.DeactivateForTest();
            component.HasPositionUpdateTick.ShouldBeFalse();
            component.Destroy(true);
        });
    }

    private static void WithFakeAudioManager(Action<AudioManager> test)
    {
        bool previousArchitecture = AudioSettings.AudioArchitectureV2;
        try
        {
            AudioSettings.AudioArchitectureV2 = true;
            AudioBackendRegistry.RegisterTransport(TestTransport, () => new FakeAudioTeardownTransport());
            AudioManager manager = new()
            {
                DefaultTransport = TestTransport,
                DefaultEffects = EAudioEffects.Passthrough
            };
            test(manager);
        }
        finally
        {
            AudioSettings.AudioArchitectureV2 = previousArchitecture;
        }
    }
}
