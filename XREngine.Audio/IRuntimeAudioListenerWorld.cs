using XREngine.Data.Core;

namespace XREngine.Audio;

public interface IRuntimeAudioListenerWorld
{
    EventList<ListenerContext> Listeners { get; }
}
