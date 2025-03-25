using System.Diagnostics;

namespace XREngine.Audio
{
    public class AudioManager
    {
        private readonly EventList<ListenerContext> _listeners = [];
        public IEventListReadOnly<ListenerContext> Listeners => _listeners;

        public int SampleRate { get; set; } = 44100;

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

        public AudioManager() { }
    }
}