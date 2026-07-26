namespace XREngine.Audio;

public interface IRuntimeAudioListenerWorld
{
    IEnumerable<object> AudioListeners { get; }
    void AddAudioListener(object listener);
    void RemoveAudioListener(object listener);
}
