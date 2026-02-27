using System.Diagnostics;
using XREngine.Data.Core;

namespace XREngine.Audio
{
    public enum AudioTransportType
    {
        OpenAL,
        NAudio,
    }

    public enum AudioEffectsType
    {
        OpenAL_EFX,
        SteamAudio,
        Passthrough,
    }

    public class AudioManager : XRBase
    {
        private readonly EventList<ListenerContext> _listeners = [];
        private int _sampleRate = 44100;
        private bool _enabled = true;
        private float _gainScale = 1.0f;
        private AudioTransportType _defaultTransport = AudioTransportType.OpenAL;
        private AudioEffectsType _defaultEffects = AudioEffectsType.OpenAL_EFX;

        public IEventListReadOnly<ListenerContext> Listeners => _listeners;

        public int SampleRate
        {
            get => _sampleRate;
            set => SetField(ref _sampleRate, value);
        }
        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public float GainScale
        {
            get => _gainScale;
            set => SetField(ref _gainScale, value);
        }

        public AudioTransportType DefaultTransport
        {
            get => _defaultTransport;
            set => SetField(ref _defaultTransport, value);
        }

        public AudioEffectsType DefaultEffects
        {
            get => _defaultEffects;
            set => SetField(ref _defaultEffects, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                //case nameof(SampleRate):
                //{
                //    Debug.WriteLine($"Sample rate changed to {SampleRate}Hz for {_listeners.Count} listeners.");
                //    foreach (var listener in _listeners)
                //        listener.SampleRate = SampleRate;
                //    break;
                //}
                case nameof(Enabled):
                {
                    Debug.WriteLine($"Audio {(Enabled ? "enabled" : "disabled")} for {_listeners.Count} listeners.");
                    foreach (var listener in _listeners)
                        listener.Enabled = Enabled;
                    break;
                }
                case nameof(GainScale):
                {
                    foreach (var listener in _listeners)
                        listener.GainScale = GainScale;
                    break;
                }
            }
        }

        private void OnContextDisposed(ListenerContext listener)
        {
            listener.Disposed -= OnContextDisposed;
            _listeners.Remove(listener);
        }
        public ListenerContext NewListener(string? name = null)
        {
            ListenerContext listener;

            if (AudioSettings.AudioArchitectureV2)
            {
                var (transportType, effectsType) = ValidateCombo(DefaultTransport, DefaultEffects);

                IAudioTransport transport = transportType switch
                {
                    AudioTransportType.OpenAL => new OpenALTransport(),
                    AudioTransportType.NAudio => CreateNAudioTransport(),
                    _ => CreateFallbackOpenAlTransport($"Unknown audio transport '{transportType}'. Falling back to OpenAL."),
                };

                IAudioEffectsProcessor effects = CreateEffectsProcessor(effectsType, transport);

                listener = new ListenerContext(transport, effects) { Name = name };
            }
            else
            {
                listener = new() { Name = name };
            }

            listener.Disposed += OnContextDisposed;
            _listeners.Add(listener);
            if (_listeners.Count > 1)
                Debug.WriteLine($"{_listeners.Count} listeners created.");
            return listener;
        }

        /// <summary>
        /// Validates a transport/effects combination and auto-corrects invalid pairings.
        /// </summary>
        public static (AudioTransportType Transport, AudioEffectsType Effects) ValidateCombo(
            AudioTransportType transport,
            AudioEffectsType effects)
        {
            // OpenAL_EFX requires OpenAL transport — EFX needs an active OpenAL context.
            if (effects == AudioEffectsType.OpenAL_EFX && transport != AudioTransportType.OpenAL)
            {
                Debug.WriteLine($"[AudioManager] {effects} requires OpenAL transport, but '{transport}' was selected. Auto-correcting to Passthrough.");
                effects = AudioEffectsType.Passthrough;
            }

            return (transport, effects);
        }

        private static IAudioEffectsProcessor CreateEffectsProcessor(AudioEffectsType effectsType, IAudioTransport transport)
        {
            return effectsType switch
            {
                AudioEffectsType.OpenAL_EFX when transport is OpenALTransport openAl => new OpenALEfxProcessor(openAl),
                AudioEffectsType.OpenAL_EFX => CreatePassthroughFallback("OpenAL_EFX requires OpenALTransport but transport is incompatible. Falling back to Passthrough."),
                AudioEffectsType.Passthrough => new PassthroughProcessor(),
                AudioEffectsType.SteamAudio => CreateSteamAudioProcessor(transport),
                _ => CreatePassthroughFallback($"Unknown audio effects '{effectsType}'. Falling back to Passthrough."),
            };
        }

        private static PassthroughProcessor CreatePassthroughFallback(string reason)
        {
            Debug.WriteLine($"[AudioManager] {reason}");
            return new PassthroughProcessor();
        }

        private static IAudioEffectsProcessor CreateSteamAudioProcessor(IAudioTransport transport)
        {
            try
            {
                return new Steam.SteamAudioProcessor();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioManager] Failed to create SteamAudioProcessor: {ex.Message}. Falling back to Passthrough.");
                return new PassthroughProcessor();
            }
        }

        private static OpenALTransport CreateFallbackOpenAlTransport(string reason)
        {
            Debug.WriteLine(reason);
            return new OpenALTransport();
        }

        private static NAudioTransport CreateNAudioTransport()
        {
            var transport = new NAudioTransport();
            try
            {
                transport.Open();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioManager] NAudioTransport.Open() failed: {ex.Message}. Transport will work without output device.");
            }
            return transport;
        }

        public void FadeIn(float fadeSeconds, Action? onComplete = null)
        {
            void FadeCompleted(ListenerContext l)
            {
                l.FadeCompleted -= FadeCompleted;
                if (_listeners.All(x => x.FadeInSeconds == null))
                    onComplete?.Invoke();
            }
            foreach (var listener in _listeners)
            {
                listener.FadeInSeconds = fadeSeconds;
                if (onComplete is not null)
                    listener.FadeCompleted += FadeCompleted;
            }
        }

        public void FadeOut(float fadeSeconds, Action? onComplete = null)
        {
            void FadeCompleted(ListenerContext l)
            {
                l.FadeCompleted -= FadeCompleted;
                if (_listeners.All(x => x.FadeInSeconds == null))
                    onComplete?.Invoke();
            }
            foreach (var listener in _listeners)
            {
                listener.FadeInSeconds = -fadeSeconds;
                if (onComplete is not null)
                    listener.FadeCompleted += FadeCompleted;
            }
        }

        public void Tick(float deltaTime)
        {
            foreach (var listener in _listeners)
                listener.Tick(deltaTime);
        }

        /// <summary>
        /// Tears down and recreates every active listener using the current
        /// <see cref="DefaultTransport"/> / <see cref="DefaultEffects"/> settings.
        /// Use after changing the global transport or effects type at runtime.
        /// </summary>
        /// <remarks>
        /// Existing <see cref="AudioSource"/>-to-listener bindings are lost; callers
        /// are expected to re-acquire listeners from the new set.
        /// </remarks>
        public void RecreateListeners()
        {
            if (!AudioSettings.AudioArchitectureV2)
            {
                Debug.WriteLine("[AudioManager] RecreateListeners only supported under V2 architecture.");
                return;
            }

            // Snapshot current listener names for recreation.
            var names = _listeners.Select(l => l.Name).ToList();

            // Dispose all existing listeners (each fires Disposed → removes itself).
            while (_listeners.Count > 0)
            {
                var last = _listeners[^1];
                last.Dispose();
            }

            // Recreate with updated transport/effects.
            foreach (var name in names)
                NewListener(name);

            Debug.WriteLine($"[AudioManager] Recreated {names.Count} listener(s) with Transport={DefaultTransport}, Effects={DefaultEffects}.");
        }

        public AudioManager() { }
    }
}