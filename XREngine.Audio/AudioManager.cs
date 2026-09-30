using System.Diagnostics;
using XREngine.Data.Core;

namespace XREngine.Audio
{
    public class AudioManager : XRBase
    {
        private readonly EventList<ListenerContext> _listeners = [];
        private int _sampleRate = 44100;
        private bool _enabled = true;
        private float _gainScale = 1.0f;
        private EAudioTransport _defaultTransport = EAudioTransport.OpenAL;
        private EAudioEffects _defaultEffects = EAudioEffects.OpenAL_EFX;

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

        public EAudioTransport DefaultTransport
        {
            get => _defaultTransport;
            set => SetField(ref _defaultTransport, value);
        }

        public EAudioEffects DefaultEffects
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

                IAudioTransport transport = AudioBackendRegistry.CreateTransport(transportType);
                IAudioEffectsProcessor effects;
                try
                {
                    effects = CreateEffectsProcessor(effectsType, transport);
                }
                catch
                {
                    transport.Dispose();
                    throw;
                }

                try
                {
                    listener = new ListenerContext(transport, effects) { Name = name };
                }
                catch
                {
                    effects.Dispose();
                    transport.Dispose();
                    throw;
                }
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
        public static (EAudioTransport Transport, EAudioEffects Effects) ValidateCombo(
            EAudioTransport transport,
            EAudioEffects effects)
        {
            // OpenAL_EFX requires OpenAL transport — EFX needs an active OpenAL context.
            if (effects == EAudioEffects.OpenAL_EFX && transport != EAudioTransport.OpenAL)
            {
                Debug.WriteLine($"[AudioManager] {effects} requires OpenAL transport, but '{transport}' was selected. Auto-correcting to Passthrough.");
                effects = EAudioEffects.Passthrough;
            }

            return (transport, effects);
        }

        private static IAudioEffectsProcessor CreateEffectsProcessor(EAudioEffects effectsType, IAudioTransport transport)
        {
            return effectsType switch
            {
                EAudioEffects.OpenAL_EFX when transport is IAudioListenerBackend => AudioBackendRegistry.CreateEffects(effectsType, transport),
                EAudioEffects.OpenAL_EFX => throw new InvalidOperationException("OpenAL EFX requires a spatial OpenAL transport."),
                EAudioEffects.Passthrough => new PassthroughProcessor(),
                EAudioEffects.SteamAudio => AudioBackendRegistry.CreateEffects(effectsType, transport),
                _ => throw new ArgumentOutOfRangeException(nameof(effectsType), effectsType, "Unknown audio effects processor."),
            };
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
