namespace XREngine.Components
{
    public partial class MicrophoneComponent
    {
        public abstract class AudioConverter
        {
            public void ModifyBuffer(byte[] buffer, int bitsPerSample, int sampleRate)
            {
                if (buffer is null || buffer.Length == 0)
                    return;

                ConvertBuffer(buffer, bitsPerSample, sampleRate);
            }
            protected abstract void ConvertBuffer(byte[] buffer, int bitsPerSample, int sampleRate);
        }






    }
}
