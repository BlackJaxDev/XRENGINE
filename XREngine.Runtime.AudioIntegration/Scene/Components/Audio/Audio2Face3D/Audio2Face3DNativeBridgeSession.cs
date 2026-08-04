using System.Text;

namespace XREngine.Components
{
    public sealed class Audio2Face3DNativeBridgeSession : IDisposable
    {
        private readonly nint _sessionHandle;
        private readonly Audio2Face3DNativeBridge.NativeApi _api;
        private bool _disposed;
        private string[]? _blendshapeNames;
        private string[]? _emotionNames;

        internal Audio2Face3DNativeBridgeSession(nint sessionHandle, Audio2Face3DNativeBridge.NativeApi api)
        {
            _sessionHandle = sessionHandle;
            _api = api;
        }

        public bool TrySubmitPcm16(short[] samples, int sampleRate, out string? error)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (samples is null || samples.Length == 0)
            {
                error = null;
                return true;
            }

            EAudio2XBridgeResult result = _api.SubmitPcm16Mono(_sessionHandle, samples, samples.Length, sampleRate);
            if (result == EAudio2XBridgeResult.Success)
            {
                error = null;
                return true;
            }

            error = Audio2Face3DNativeBridge.GetLastError(_sessionHandle, $"Audio2XBridge.Native rejected audio submission ({result}).");
            return false;
        }

        public EAudio2Face3DNativePollResult PollBlendshapeFrame(out string[]? blendshapeNames, out float[]? weights, out string? error)
            => PollFrame(_api.GetBlendshapeLayout, _api.PollBlendshapeWeights, ref _blendshapeNames, out blendshapeNames, out weights, out error);

        public EAudio2Face3DNativePollResult PollEmotionFrame(out string[]? emotionNames, out float[]? weights, out string? error)
            => PollFrame(_api.GetEmotionLayout, _api.PollEmotionWeights, ref _emotionNames, out emotionNames, out weights, out error);

        public void Dispose()
        {
            if (_disposed)
                return;

            _api.DestroySession(_sessionHandle);
            _disposed = true;
        }

        private EAudio2Face3DNativePollResult PollFrame(
            Audio2Face3DNativeBridge.NativeApi.GetLayoutDelegate getLayout,
            Audio2Face3DNativeBridge.NativeApi.PollWeightsDelegate pollWeights,
            ref string[]? cachedNames,
            out string[]? names,
            out float[]? weights,
            out string? error)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            names = null;
            weights = null;

            if (!EnsureLayout(getLayout, ref cachedNames, out error) || cachedNames is null || cachedNames.Length == 0)
                return string.IsNullOrWhiteSpace(error) ? EAudio2Face3DNativePollResult.NoData : EAudio2Face3DNativePollResult.Error;

            float[] nextWeights = new float[cachedNames.Length];
            EAudio2XBridgeResult result = pollWeights(_sessionHandle, nextWeights, nextWeights.Length, out int count);
            if (result == EAudio2XBridgeResult.NoData)
            {
                error = null;
                return EAudio2Face3DNativePollResult.NoData;
            }

            if (result != EAudio2XBridgeResult.Success)
            {
                error = Audio2Face3DNativeBridge.GetLastError(_sessionHandle, $"Audio2XBridge.Native failed to poll weights ({result}).");
                return EAudio2Face3DNativePollResult.Error;
            }

            if (count != cachedNames.Length)
            {
                error = $"Audio2XBridge.Native returned {count} weights for a layout that expected {cachedNames.Length}.";
                return EAudio2Face3DNativePollResult.Error;
            }

            names = cachedNames;
            weights = nextWeights;
            error = null;
            return EAudio2Face3DNativePollResult.Success;
        }

        private bool EnsureLayout(
            Audio2Face3DNativeBridge.NativeApi.GetLayoutDelegate getLayout,
            ref string[]? cachedNames,
            out string? error)
        {
            if (cachedNames is not null)
            {
                error = null;
                return true;
            }

            EAudio2XBridgeResult sizeResult = getLayout(_sessionHandle, null, 0, out int requiredBytes, out int count);
            if (sizeResult != EAudio2XBridgeResult.Success && sizeResult != EAudio2XBridgeResult.NoData)
            {
                error = Audio2Face3DNativeBridge.GetLastError(_sessionHandle, $"Audio2XBridge.Native failed to resolve the output layout ({sizeResult}).");
                return false;
            }

            if (requiredBytes <= 1 || count <= 0)
            {
                cachedNames = [];
                error = null;
                return true;
            }

            byte[] buffer = new byte[requiredBytes];
            EAudio2XBridgeResult layoutResult = getLayout(_sessionHandle, buffer, buffer.Length, out requiredBytes, out count);
            if (layoutResult != EAudio2XBridgeResult.Success)
            {
                error = Audio2Face3DNativeBridge.GetLastError(_sessionHandle, $"Audio2XBridge.Native failed to fetch the output layout ({layoutResult}).");
                return false;
            }

            int terminatorIndex = Array.IndexOf(buffer, (byte)0);
            int byteCount = terminatorIndex >= 0 ? terminatorIndex : buffer.Length;
            string layout = Encoding.UTF8.GetString(buffer, 0, byteCount);
            cachedNames = [.. layout.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            error = null;
            return true;
        }
    }
}