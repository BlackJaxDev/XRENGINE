using XREngine.Input;
using XREngine.Rendering;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Installs desktop VR adapters while an application composition is active and restores the
/// previous runtime services after its worlds and input consumers have been torn down.
/// </summary>
internal sealed class DesktopVrServicesLease : IDisposable
{
    private readonly IRuntimeVrInputServices _previousInput;
    private readonly IRuntimeVrStateServices _previousState;
    private readonly IRuntimeVrRenderingServices _previousRendering;
    private readonly IRuntimeVrLifecycleServices _previousLifecycle;
    private EngineRuntimeVrInputServices? _ownedInput;
    private EngineRuntimeVrStateServices? _ownedState;
    private EngineRuntimeVrRenderingServices? _ownedRendering;
    private int _disposed;

    public DesktopVrServicesLease(RuntimeApplicationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Capture every slot before a provider constructor can subscribe to native events or
        // replace the lifecycle service as a construction side effect.
        _previousInput = RuntimeVrInputServices.Current;
        _previousState = RuntimeVrStateServices.Current;
        _previousRendering = RuntimeVrRenderingServices.Current;
        _previousLifecycle = RuntimeEngine.VRState.LifecycleServices;

        try
        {
            if (profile.AdapterProfile.HasFlag(RuntimeAdapterProfile.Input))
            {
                _ownedInput = new EngineRuntimeVrInputServices();
                RuntimeVrInputServices.Current = _ownedInput;

                _ownedState = new EngineRuntimeVrStateServices();
                RuntimeVrStateServices.Current = _ownedState;
            }

            if (profile.AllowsVr)
            {
                _ownedRendering = new EngineRuntimeVrRenderingServices();
                RuntimeVrRenderingServices.Current = _ownedRendering;
            }
        }
        catch (Exception installationFailure)
        {
            try
            {
                Dispose();
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "Desktop VR service installation failed and rollback was incomplete.",
                    installationFailure,
                    rollbackFailure);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        List<Exception>? failures = null;
        Restore(() => RuntimeVrRenderingServices.Current = _previousRendering, ref failures);
        Restore(() => RuntimeVrStateServices.Current = _previousState, ref failures);
        Restore(() => RuntimeVrInputServices.Current = _previousInput, ref failures);
        Restore(() => RuntimeEngine.VRState.LifecycleServices = _previousLifecycle, ref failures);

        // Slot setters detach their forwarding subscriptions; providers then release their
        // separate native-device and action/session hooks.
        if (_ownedState is not null)
            Restore(_ownedState.Dispose, ref failures);
        if (_ownedInput is not null)
            Restore(_ownedInput.Dispose, ref failures);

        _ownedRendering = null;
        _ownedState = null;
        _ownedInput = null;

        if (failures is [Exception failure])
            throw failure;
        if (failures is { Count: > 1 })
            throw new AggregateException("Desktop VR services failed to restore cleanly.", failures);
    }

    private static void Restore(Action action, ref List<Exception>? failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
    }
}
