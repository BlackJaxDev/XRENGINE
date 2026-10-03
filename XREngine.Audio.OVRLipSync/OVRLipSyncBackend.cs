using System.Buffers;
using static XREngine.Components.OVRLipSync;

namespace XREngine.Components;

/// <summary>Installs the Meta lip-sync implementation without exposing native types to scene components.</summary>
public static class OVRLipSyncBackend
{
    private static readonly object Gate = new();
    private static int _sessionCount;
    private static int _sampleRate;
    private static int _bufferSize;

    public static void Register() => LipSyncSessionRegistry.Register(CreateSession);

    private static ILipSyncSession CreateSession(int sampleRate, int bufferSize)
    {
        if (sampleRate <= 0 || bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Lip-sync sample rate and buffer size must be positive.");

        lock (Gate)
        {
            if (_sessionCount == 0)
            {
                Check(ovrLipSyncDll_Initialize(sampleRate, bufferSize), "initialize");
                _sampleRate = sampleRate;
                _bufferSize = bufferSize;
            }
            else if (_sampleRate != sampleRate || _bufferSize != bufferSize)
            {
                throw new InvalidOperationException("Active lip-sync sessions use a different sample rate or buffer size.");
            }

            var context = new ovrLipSyncContext();
            try
            {
                Check(ovrLipSyncDll_CreateContextEx(
                    ref context,
                    ovrLipSyncContextProvider.ovrLipSyncContextProvider_EnhancedWithLaughter,
                    sampleRate,
                    true), "create context");
                _sessionCount++;
                return new Session(context);
            }
            catch
            {
                if (_sessionCount == 0)
                    Check(ovrLipSyncDll_Shutdown(), "shutdown after context creation failure");
                throw;
            }
        }
    }

    private static void Check(ovrLipSyncResult result, string operation)
    {
        if (result != ovrLipSyncResult.ovrLipSyncSuccess)
            throw new InvalidOperationException($"OVRLipSync could not {operation}: {result}.");
    }

    private sealed class Session(ovrLipSyncContext context) : ILipSyncSession
    {
        private ovrLipSyncContext _context = context;

        public void SetSmoothing(int amount)
        {
            lock (Gate)
            {
                EnsureActive();
                Check(ovrLipSyncDll_SendSignal(_context, ovrLipSyncSignals.ovrLipSyncSignals_VisemeSmoothing, amount, 0), "set smoothing");
            }
        }

        public void Process(byte[] samples, bool stereo, float[] visemes, ref float laughterScore)
        {
            ArgumentNullException.ThrowIfNull(samples);
            float[] converted = ArrayPool<float>.Shared.Rent(samples.Length);
            try
            {
                for (int i = 0; i < samples.Length; i++)
                    converted[i] = samples[i] / 255.0f;
                Process(converted, samples.Length, stereo, visemes, ref laughterScore);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(converted);
            }
        }

        public unsafe void Process(short[] samples, bool stereo, float[] visemes, ref float laughterScore)
        {
            ArgumentNullException.ThrowIfNull(samples);
            ValidateVisemes(visemes);
            lock (Gate)
            {
                EnsureActive();
                fixed (short* data = samples)
                    ProcessNative((nint)data, samples.Length,
                        stereo ? ovrLipSyncAudioDataType.ovrLipSyncAudioDataType_S16_Stereo : ovrLipSyncAudioDataType.ovrLipSyncAudioDataType_S16_Mono,
                        visemes, ref laughterScore);
            }
        }

        public void Process(float[] samples, bool stereo, float[] visemes, ref float laughterScore)
        {
            ArgumentNullException.ThrowIfNull(samples);
            Process(samples, samples.Length, stereo, visemes, ref laughterScore);
        }

        private unsafe void Process(float[] samples, int length, bool stereo, float[] visemes, ref float laughterScore)
        {
            ArgumentNullException.ThrowIfNull(samples);
            ValidateVisemes(visemes);
            lock (Gate)
            {
                EnsureActive();
                fixed (float* data = samples)
                    ProcessNative((nint)data, length,
                        stereo ? ovrLipSyncAudioDataType.ovrLipSyncAudioDataType_F32_Stereo : ovrLipSyncAudioDataType.ovrLipSyncAudioDataType_F32_Mono,
                        visemes, ref laughterScore);
            }
        }

        private void ProcessNative(nint samples, int length, ovrLipSyncAudioDataType dataType, float[] visemes, ref float laughterScore)
        {
            int frameNumber = 0;
            int frameDelay = 0;
            Check(ovrLipSyncDll_ProcessFrameEx(_context.handle, samples, (uint)length, dataType,
                ref frameNumber, ref frameDelay, visemes, visemes.Length, ref laughterScore, null, 0), "process audio");
        }

        private static void ValidateVisemes(float[] visemes)
        {
            ArgumentNullException.ThrowIfNull(visemes);
            if (visemes.Length != LipSyncVisemeInfo.Count)
                throw new ArgumentException("The lip-sync viseme buffer must contain fifteen values.", nameof(visemes));
        }

        private void EnsureActive()
        {
            if (_context.handle == 0)
                throw new ObjectDisposedException(nameof(Session));
        }

        public void Dispose()
        {
            lock (Gate)
            {
                if (_context.handle == 0)
                    return;

                Check(ovrLipSyncDll_DestroyContext(_context), "destroy context");
                _context = default;
                _sessionCount--;
                if (_sessionCount == 0)
                    Check(ovrLipSyncDll_Shutdown(), "shutdown");
            }
        }
    }
}
