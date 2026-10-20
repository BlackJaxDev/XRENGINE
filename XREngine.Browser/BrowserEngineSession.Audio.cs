using XREngine.Audio.WebAudio;
using XREngine.Scene;

namespace XREngine.Browser;

internal sealed partial class BrowserEngineSession
{
    private WebAudioTransport? _audioActivationOwner;
    private bool _audioRequired;
    private bool _audioSimulationBlocked;

    public bool AudioRequired => _audioRequired;
    private bool HasAudioOwnership => _audioActivationOwner is not null;

    /// <summary>Provides an owned readiness output even when gameplay creates its listener later.</summary>
    private void InitializeAudioRequirement(XRWorld world)
    {
        _audioRequired = world.RequiresAudio;
        if (_audioRequired)
            _audioActivationOwner = new WebAudioTransport();
    }

    /// <summary>Keeps presentation alive while explicitly required output is unavailable.</summary>
    private bool UpdateAudioSimulationGate()
    {
        bool blocked = _audioRequired && !WebAudioTransport.IsReady;
        if (blocked && WebAudioTransport.HasActivationFailure)
            throw new InvalidOperationException(WebAudioTransport.ActivationFailure);
        if (blocked != _audioSimulationBlocked)
        {
            _audioSimulationBlocked = blocked;
            ResetInput();
            ResetFrameTiming();
        }
        return blocked;
    }

    private void ReleaseAudioRequirement()
    {
        _audioActivationOwner?.Dispose();
        _audioActivationOwner = null;
        _audioRequired = false;
        _audioSimulationBlocked = false;
    }

    private bool StepWithAudioUpdates(double elapsedSeconds, bool dispatchSimulation)
    {
        if (Engine.Audio.Listeners.Count == 0)
            return Engine.Time.Timer.StepFrame(elapsedSeconds, dispatchSimulation);

        int batch = WebAudioTransport.BeginSpatialUpdates();
        Exception? frameFailure = null;
        try
        {
            return Engine.Time.Timer.StepFrame(elapsedSeconds, dispatchSimulation);
        }
        catch (Exception error)
        {
            frameFailure = error;
            throw;
        }
        finally
        {
            try { WebAudioTransport.EndSpatialUpdates(batch); }
            catch (Exception audioFailure) when (frameFailure is not null)
            {
                throw new AggregateException("Browser engine frame and audio pose flush both failed.", frameFailure, audioFailure);
            }
        }
    }
}
