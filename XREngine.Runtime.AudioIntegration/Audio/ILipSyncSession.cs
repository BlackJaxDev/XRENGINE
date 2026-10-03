namespace XREngine.Components;

/// <summary>Processes PCM samples into the standard fifteen lip-sync visemes.</summary>
public interface ILipSyncSession : IDisposable
{
    void SetSmoothing(int amount);
    void Process(byte[] samples, bool stereo, float[] visemes, ref float laughterScore);
    void Process(short[] samples, bool stereo, float[] visemes, ref float laughterScore);
    void Process(float[] samples, bool stereo, float[] visemes, ref float laughterScore);
}
