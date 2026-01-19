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
            ListenerContext listener = new() { Name = name };
            listener.Disposed += OnContextDisposed;
            _listeners.Add(listener);
            if (_listeners.Count > 1)
                Debug.WriteLine($"{_listeners.Count} listeners created.");
            return listener;
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

        public AudioManager() { }
    }
}