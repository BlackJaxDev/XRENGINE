namespace XREngine.Components
{
    public static class Audio2Face3DNativeBridgeAudioConverter
    {
        public static short[] ConvertToPcm16Mono(byte[] audioData, int bitsPerSample, int sourceSampleRate, int targetSampleRate)
        {
            ArgumentNullException.ThrowIfNull(audioData);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);

            int bytesPerSample = bitsPerSample switch
            {
                8 => 1,
                16 => 2,
                32 => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample, "Only 8-bit, 16-bit, and 32-bit mono PCM microphone buffers are supported."),
            };

            if (audioData.Length == 0)
                return [];

            if (audioData.Length % bytesPerSample != 0)
                throw new ArgumentException("Audio buffer length must align with the source sample size.", nameof(audioData));

            int sourceSampleCount = audioData.Length / bytesPerSample;
            float[] normalizedSamples = new float[sourceSampleCount];

            switch (bitsPerSample)
            {
                case 8:
                    for (int i = 0; i < sourceSampleCount; i++)
                        normalizedSamples[i] = (audioData[i] - 128.0f) / 128.0f;
                    break;
                case 16:
                    for (int i = 0; i < sourceSampleCount; i++)
                    {
                        short sample = BitConverter.ToInt16(audioData, i * sizeof(short));
                        normalizedSamples[i] = sample >= 0 ? sample / (float)short.MaxValue : sample / 32768.0f;
                    }
                    break;
                case 32:
                    for (int i = 0; i < sourceSampleCount; i++)
                        normalizedSamples[i] = Math.Clamp(BitConverter.ToSingle(audioData, i * sizeof(float)), -1.0f, 1.0f);
                    break;
            }

            if (sourceSampleRate == targetSampleRate)
            {
                short[] directOutput = new short[sourceSampleCount];
                for (int i = 0; i < directOutput.Length; i++)
                    directOutput[i] = FloatToPcm16(normalizedSamples[i]);
                return directOutput;
            }

            int targetSampleCount = Math.Max(1, (int)Math.Round(sourceSampleCount * (double)targetSampleRate / sourceSampleRate, MidpointRounding.AwayFromZero));
            short[] resampledOutput = new short[targetSampleCount];
            double sourceSamplesPerTargetSample = (double)sourceSampleRate / targetSampleRate;

            for (int i = 0; i < targetSampleCount; i++)
            {
                double sourcePosition = i * sourceSamplesPerTargetSample;
                int sourceIndex0 = Math.Min((int)sourcePosition, sourceSampleCount - 1);
                int sourceIndex1 = Math.Min(sourceIndex0 + 1, sourceSampleCount - 1);
                float t = (float)(sourcePosition - sourceIndex0);
                float sample = normalizedSamples[sourceIndex0] + ((normalizedSamples[sourceIndex1] - normalizedSamples[sourceIndex0]) * t);
                resampledOutput[i] = FloatToPcm16(sample);
            }

            return resampledOutput;
        }

        private static short FloatToPcm16(float sample)
        {
            float clamped = Math.Clamp(sample, -1.0f, 1.0f);
            return (short)Math.Round(clamped * short.MaxValue, MidpointRounding.AwayFromZero);
        }
    }
}