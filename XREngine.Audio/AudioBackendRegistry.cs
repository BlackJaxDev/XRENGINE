using XREngine.Data.Core;

namespace XREngine.Audio;

/// <summary>Explicit factories installed by the application for optional audio transports.</summary>
public static class AudioBackendRegistry
{
    private static readonly Dictionary<EAudioTransport, Func<IAudioTransport>> TransportFactories = new();
    private static readonly Dictionary<EAudioEffects, Func<IAudioTransport, IAudioEffectsProcessor>> EffectsFactories = new();
    private static readonly object Sync = new();

    public static void RegisterTransport(EAudioTransport transport, Func<IAudioTransport> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Sync)
            TransportFactories[transport] = factory;
    }

    public static IAudioTransport CreateTransport(EAudioTransport transport)
    {
        Func<IAudioTransport> factory;
        lock (Sync)
        {
            if (!TransportFactories.TryGetValue(transport, out factory!))
                throw new InvalidOperationException($"Audio transport '{transport}' is not registered.");
        }

        return factory();
    }

    public static void RegisterEffects(EAudioEffects effects, Func<IAudioTransport, IAudioEffectsProcessor> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Sync)
            EffectsFactories[effects] = factory;
    }

    public static IAudioEffectsProcessor CreateEffects(EAudioEffects effects, IAudioTransport transport)
    {
        Func<IAudioTransport, IAudioEffectsProcessor> factory;
        lock (Sync)
        {
            if (!EffectsFactories.TryGetValue(effects, out factory!))
                throw new InvalidOperationException($"Audio effects processor '{effects}' is not registered.");
        }

        return factory(transport);
    }
}
