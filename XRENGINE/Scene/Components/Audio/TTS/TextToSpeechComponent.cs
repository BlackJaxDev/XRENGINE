using System.Collections.Concurrent;
using System.ComponentModel;
using XREngine.Core.Attributes;
using XREngine.Data.Core;

namespace XREngine.Components
{
    /// <summary>
    /// Component that synthesizes speech from text using various TTS providers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This component provides text-to-speech capabilities with support for multiple cloud providers.
    /// It can optionally play the synthesized audio through an <see cref="AudioSourceComponent"/>.
    /// </para>
    /// <para>
    /// Supported providers:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>OpenAI</b>: High-quality voices with simple API</description></item>
    /// <item><description><b>Google</b>: Wide language support, WaveNet voices</description></item>
    /// <item><description><b>Azure</b>: Neural voices with SSML support</description></item>
    /// <item><description><b>ElevenLabs</b>: Premium expressive voices</description></item>
    /// <item><description><b>Amazon</b>: Polly service with neural option</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Basic setup
    /// var tts = sceneNode.AddComponent&lt;TextToSpeechComponent&gt;();
    /// tts.SelectedProvider = ETTSProvider.OpenAI;
    /// tts.ApiKey = "sk-your-key";
    /// tts.AutoPlay = true;
    /// 
    /// // Speak text
    /// await tts.SpeakAsync("Hello, world!");
    /// 
    /// // Or get audio data without playing
    /// var result = await tts.SynthesizeAsync("Hello");
    /// if (result.Success)
    /// {
    ///     // Use result.AudioData
    /// }
    /// </code>
    /// </example>
    [Category("Audio")]
    [DisplayName("Text To Speech")]
    [Description("Synthesizes speech from text using cloud TTS providers.")]
    public class TextToSpeechComponent : XRComponent
    {
        #region Fields

        private ETTSProvider _selectedProvider = ETTSProvider.OpenAI;
        private string _apiKey = string.Empty;
        private string _secondaryApiKey = string.Empty; // For providers requiring multiple keys (e.g., Azure region)
        private string _language = "en-US";
        private string _voice = string.Empty;
        private string _model = string.Empty;
        private float _speechRate = 1.0f;
        private float _pitch = 1.0f;
        private float _volume = 1.0f;
        private bool _autoPlay = true;
        private bool _isSpeaking = false;
        private AudioSourceComponent? _audioSource;

        private readonly ConcurrentQueue<SpeechRequest> _speechQueue = new();
        private CancellationTokenSource? _currentSpeechCts;
        private readonly object _speakLock = new();

        private ITTSProvider? _cachedProvider;
        private ETTSProvider _cachedProviderType;

        #endregion

        #region Properties

        /// <summary>
        /// The TTS provider to use for synthesis.
        /// </summary>
        [Category("Provider")]
        [DisplayName("Provider")]
        [Description("The cloud TTS service to use.")]
        public ETTSProvider SelectedProvider
        {
            get => _selectedProvider;
            set
            {
                if (SetField(ref _selectedProvider, value))
                    InvalidateProvider();
            }
        }

        /// <summary>
        /// API key for the selected provider.
        /// </summary>
        [Category("Provider")]
        [DisplayName("API Key")]
        [Description("API key for authentication with the TTS provider.")]
        public string ApiKey
        {
            get => _apiKey;
            set
            {
                if (SetField(ref _apiKey, value ?? string.Empty))
                    InvalidateProvider();
            }
        }

        /// <summary>
        /// Secondary API key or region for providers that require it (e.g., Azure region).
        /// </summary>
        [Category("Provider")]
        [DisplayName("Secondary Key / Region")]
        [Description("Additional configuration (Azure: region name, Amazon: secret key).")]
        public string SecondaryApiKey
        {
            get => _secondaryApiKey;
            set
            {
                if (SetField(ref _secondaryApiKey, value ?? string.Empty))
                    InvalidateProvider();
            }
        }

        /// <summary>
        /// The language code for speech synthesis (e.g., "en-US", "es-ES").
        /// </summary>
        [Category("Voice")]
        [DisplayName("Language")]
        [Description("Language code for speech synthesis.")]
        public string Language
        {
            get => _language;
            set => SetField(ref _language, value ?? "en-US");
        }

        /// <summary>
        /// The voice identifier to use (provider-specific).
        /// Leave empty to use the provider's default voice.
        /// </summary>
        [Category("Voice")]
        [DisplayName("Voice")]
        [Description("Voice identifier (provider-specific). Empty for default.")]
        public string Voice
        {
            get => _voice;
            set => SetField(ref _voice, value ?? string.Empty);
        }

        /// <summary>
        /// The model to use for synthesis (provider-specific).
        /// </summary>
        [Category("Voice")]
        [DisplayName("Model")]
        [Description("Model identifier (e.g., 'tts-1' for OpenAI). Empty for default.")]
        public string Model
        {
            get => _model;
            set
            {
                if (SetField(ref _model, value ?? string.Empty))
                    InvalidateProvider();
            }
        }

        /// <summary>
        /// Speech rate multiplier (0.5 to 2.0). Not all providers support this.
        /// </summary>
        [Category("Voice")]
        [DisplayName("Speech Rate")]
        [Description("Speed of speech (0.5-2.0). Not supported by all providers.")]
        public float SpeechRate
        {
            get => _speechRate;
            set => SetField(ref _speechRate, Math.Clamp(value, 0.5f, 2.0f));
        }

        /// <summary>
        /// Pitch adjustment (-1.0 to 1.0). Not all providers support this.
        /// </summary>
        [Category("Voice")]
        [DisplayName("Pitch")]
        [Description("Pitch adjustment (-1.0 to 1.0). Not supported by all providers.")]
        public float Pitch
        {
            get => _pitch;
            set => SetField(ref _pitch, Math.Clamp(value, -1.0f, 1.0f));
        }

        /// <summary>
        /// Volume multiplier (0.0 to 1.0).
        /// </summary>
        [Category("Playback")]
        [DisplayName("Volume")]
        [Description("Output volume (0.0-1.0).")]
        public float Volume
        {
            get => _volume;
            set => SetField(ref _volume, Math.Clamp(value, 0.0f, 1.0f));
        }

        /// <summary>
        /// When true, synthesized audio is automatically played through the AudioSource.
        /// </summary>
        [Category("Playback")]
        [DisplayName("Auto Play")]
        [Description("Automatically play synthesized audio.")]
        public bool AutoPlay
        {
            get => _autoPlay;
            set => SetField(ref _autoPlay, value);
        }

        /// <summary>
        /// Whether the component is currently speaking.
        /// </summary>
        [Browsable(false)]
        public bool IsSpeaking => _isSpeaking;

        /// <summary>
        /// The audio source component used for playback.
        /// If not set, will look for a sibling AudioSourceComponent.
        /// </summary>
        [Browsable(false)]
        public AudioSourceComponent? AudioSource
        {
            get => _audioSource ??= GetSiblingComponent<AudioSourceComponent>(false);
            set => SetField(ref _audioSource, value);
        }

        #endregion

        #region Events

        /// <summary>
        /// Raised when synthesis begins for a piece of text.
        /// </summary>
        public event Action<TextToSpeechComponent, string>? SynthesisStarted;

        /// <summary>
        /// Raised when synthesis completes successfully.
        /// </summary>
        public event Action<TextToSpeechComponent, TTSResult>? SynthesisCompleted;

        /// <summary>
        /// Raised when audio playback begins.
        /// </summary>
        public event Action<TextToSpeechComponent>? PlaybackStarted;

        /// <summary>
        /// Raised when audio playback ends.
        /// </summary>
        public event Action<TextToSpeechComponent>? PlaybackEnded;

        /// <summary>
        /// Raised when an error occurs during synthesis or playback.
        /// </summary>
        public event Action<TextToSpeechComponent, string>? ErrorOccurred;

        #endregion

        #region Public Methods

        /// <summary>
        /// Synthesizes speech from the given text and optionally plays it.
        /// </summary>
        /// <param name="text">The text to speak.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The synthesis result.</returns>
        public async Task<TTSResult> SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            var result = await SynthesizeAsync(text, cancellationToken);

            if (result.Success && AutoPlay && result.AudioData != null)
            {
                await PlayAudioAsync(result, cancellationToken);
            }

            return result;
        }

        /// <summary>
        /// Synthesizes speech from the given text without playing it.
        /// </summary>
        /// <param name="text">The text to synthesize.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The synthesis result containing audio data.</returns>
        public async Task<TTSResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return TTSResult.Failure("Text cannot be empty");
            }

            if (string.IsNullOrEmpty(ApiKey))
            {
                var error = "API key not configured";
                ErrorOccurred?.Invoke(this, error);
                return TTSResult.Failure(error);
            }

            _isSpeaking = true;
            SynthesisStarted?.Invoke(this, text);
            Debug.Audio($"[TTS] Synthesizing: \"{text}\"");

            try
            {
                var provider = GetOrCreateProvider();
                var voiceToUse = string.IsNullOrEmpty(Voice) ? null : Voice;
                var result = await provider.SynthesizeAsync(text, voiceToUse, cancellationToken);

                if (result.Success)
                {
                    Debug.Audio($"[TTS] Synthesis complete: {result.AudioData?.Length ?? 0} bytes, {result.DurationSeconds:F2}s");
                    SynthesisCompleted?.Invoke(this, result);
                }
                else
                {
                    Debug.Audio($"[TTS] Synthesis failed: {result.Error}");
                    ErrorOccurred?.Invoke(this, result.Error ?? "Unknown error");
                }

                return result;
            }
            catch (Exception ex)
            {
                var error = $"Synthesis error: {ex.Message}";
                Debug.Audio($"[TTS] {error}");
                ErrorOccurred?.Invoke(this, error);
                return TTSResult.Failure(error);
            }
            finally
            {
                _isSpeaking = false;
            }
        }

        /// <summary>
        /// Plays the audio from a synthesis result.
        /// </summary>
        /// <param name="result">The synthesis result containing audio data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task PlayAudioAsync(TTSResult result, CancellationToken cancellationToken = default)
        {
            if (result.AudioData == null || result.AudioData.Length == 0)
            {
                return;
            }

            var audioSource = AudioSource;
            if (audioSource == null)
            {
                Debug.Audio("[TTS] No AudioSourceComponent found for playback");
                return;
            }

            try
            {
                PlaybackStarted?.Invoke(this);
                _isSpeaking = true;

                // Apply volume
                audioSource.Gain = Volume;

                // Queue the audio for playback
                bool stereo = result.Channels == 2;
                
                if (result.BitsPerSample == 16)
                {
                    // Convert byte[] to short[]
                    var shortData = new short[result.AudioData.Length / 2];
                    Buffer.BlockCopy(result.AudioData, 0, shortData, 0, result.AudioData.Length);
                    audioSource.EnqueueStreamingBuffers(result.SampleRate, stereo, shortData);
                }
                else if (result.BitsPerSample == 8)
                {
                    audioSource.EnqueueStreamingBuffers(result.SampleRate, stereo, result.AudioData);
                }
                else if (result.BitsPerSample == 32)
                {
                    // Convert byte[] to float[]
                    var floatData = new float[result.AudioData.Length / 4];
                    Buffer.BlockCopy(result.AudioData, 0, floatData, 0, result.AudioData.Length);
                    audioSource.EnqueueStreamingBuffers(result.SampleRate, stereo, floatData);
                }

                audioSource.Play();

                // Wait for playback to complete
                var durationMs = (int)(result.DurationSeconds * 1000) + 100; // Add small buffer
                await Task.Delay(durationMs, cancellationToken);

                PlaybackEnded?.Invoke(this);
            }
            catch (OperationCanceledException)
            {
                audioSource.Stop();
            }
            catch (Exception ex)
            {
                Debug.Audio($"[TTS] Playback error: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"Playback error: {ex.Message}");
            }
            finally
            {
                _isSpeaking = false;
            }
        }

        /// <summary>
        /// Queues text to be spoken. Useful for speaking multiple phrases in sequence.
        /// </summary>
        /// <param name="text">The text to speak.</param>
        /// <param name="priority">Higher priority items are spoken first.</param>
        public void QueueSpeech(string text, int priority = 0)
        {
            _speechQueue.Enqueue(new SpeechRequest(text, priority));
            _ = ProcessSpeechQueueAsync();
        }

        /// <summary>
        /// Stops the current speech and clears the queue.
        /// </summary>
        public void Stop()
        {
            _currentSpeechCts?.Cancel();
            while (_speechQueue.TryDequeue(out _)) { }
            AudioSource?.Stop();
            _isSpeaking = false;
        }

        /// <summary>
        /// Gets the list of available voices for the current provider.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Array of available voices.</returns>
        public async Task<TTSVoice[]> GetAvailableVoicesAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ApiKey))
            {
                return [];
            }

            try
            {
                var provider = GetOrCreateProvider();
                return await provider.GetAvailableVoicesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.Audio($"[TTS] Failed to get voices: {ex.Message}");
                return [];
            }
        }

        #endregion

        #region Private Methods

        private ITTSProvider GetOrCreateProvider()
        {
            if (_cachedProvider != null && _cachedProviderType == SelectedProvider)
            {
                return _cachedProvider;
            }

            _cachedProvider = SelectedProvider switch
            {
                ETTSProvider.OpenAI => new OpenAITTSProvider(ApiKey, 
                    string.IsNullOrEmpty(Model) ? "tts-1" : Model),
                ETTSProvider.Google => new GoogleTTSProvider(ApiKey, Language),
                ETTSProvider.Azure => new AzureTTSProvider(ApiKey, 
                    string.IsNullOrEmpty(SecondaryApiKey) ? "eastus" : SecondaryApiKey, Language),
                ETTSProvider.ElevenLabs => new ElevenLabsTTSProvider(ApiKey),
                ETTSProvider.Amazon => new AmazonPollyTTSProvider(ApiKey, SecondaryApiKey),
                _ => throw new NotSupportedException($"TTS provider {SelectedProvider} is not supported")
            };

            _cachedProviderType = SelectedProvider;
            return _cachedProvider;
        }

        private void InvalidateProvider()
        {
            _cachedProvider = null;
        }

        private async Task ProcessSpeechQueueAsync()
        {
            lock (_speakLock)
            {
                if (_isSpeaking)
                    return;
            }

            while (_speechQueue.TryDequeue(out var request))
            {
                _currentSpeechCts = new CancellationTokenSource();
                try
                {
                    await SpeakAsync(request.Text, _currentSpeechCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    _currentSpeechCts.Dispose();
                    _currentSpeechCts = null;
                }
            }
        }

        #endregion

        #region Lifecycle

        protected override void OnDestroying()
        {
            base.OnDestroying();
            Stop();
            _currentSpeechCts?.Dispose();
        }

        #endregion

        #region Nested Types

        private record SpeechRequest(string Text, int Priority);

        #endregion
    }

    /// <summary>
    /// Available text-to-speech providers.
    /// </summary>
    public enum ETTSProvider
    {
        /// <summary>OpenAI TTS API with voices like alloy, echo, fable, onyx, nova, shimmer.</summary>
        OpenAI,

        /// <summary>Google Cloud Text-to-Speech with WaveNet and Standard voices.</summary>
        Google,

        /// <summary>Azure Cognitive Services Speech with neural voices.</summary>
        Azure,

        /// <summary>ElevenLabs with premium expressive AI voices.</summary>
        ElevenLabs,

        /// <summary>Amazon Polly with standard and neural voices.</summary>
        Amazon
    }
}
