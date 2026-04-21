using NAudio.Wave;

namespace XREngine.Components
{
    public partial class MicrophoneComponent
    {
        public class ElevenLabsConverter : AudioConverter
        {
            private readonly HttpClient _httpClient = new();
            private readonly string _apiKey;
            private string _voiceId;
            private string _modelId;
            private float _stability;
            private float _similarityBoost;
            private float _style;
            private bool _useSpeakerBoost;
            private int _latencyOptimization;
            private bool _enableStreaming;
            private int _maxRetries;
            private int _retryDelayMs;
            private readonly Queue<byte[]> _processingQueue = new();
            private readonly Lock _queueLock = new();
            private bool _isProcessing = false;
            private readonly SemaphoreSlim _semaphore = new(1, 1);

            public ElevenLabsConverter(string apiKey, string voiceId = "21m00Tcm4TlvDq8ikWAM", string modelId = "eleven_english_sts_v2")
            {
                _apiKey = apiKey;
                _voiceId = voiceId;
                _modelId = modelId;
                _stability = 0.5f;
                _similarityBoost = 0.75f;
                _style = 0.0f;
                _useSpeakerBoost = true;
                _latencyOptimization = 0;
                _enableStreaming = false;
                _maxRetries = 3;
                _retryDelayMs = 1000;

                _httpClient.DefaultRequestHeaders.Add("xi-api-key", _apiKey);
                _httpClient.DefaultRequestHeaders.Add("Accept", "audio/mpeg");
            }

            // Configuration properties
            public string VoiceId
            {
                get => _voiceId;
                set
                {
                    if (_voiceId != value)
                    {
                        _voiceId = value;
                        // Clear queue when voice changes
                        using (_queueLock.EnterScope())
                        {
                            _processingQueue.Clear();
                        }
                    }
                }
            }

            public string ModelId
            {
                get => _modelId;
                set
                {
                    if (_modelId != value)
                    {
                        _modelId = value;
                        // Clear queue when model changes
                        using (_queueLock.EnterScope())
                        {
                            _processingQueue.Clear();
                        }
                    }
                }
            }

            public float Stability
            {
                get => _stability;
                set => _stability = Math.Clamp(value, 0.0f, 1.0f);
            }

            public float SimilarityBoost
            {
                get => _similarityBoost;
                set => _similarityBoost = Math.Clamp(value, 0.0f, 1.0f);
            }

            public float Style
            {
                get => _style;
                set => _style = Math.Clamp(value, 0.0f, 1.0f);
            }

            public bool UseSpeakerBoost
            {
                get => _useSpeakerBoost;
                set => _useSpeakerBoost = value;
            }

            public int LatencyOptimization
            {
                get => _latencyOptimization;
                set => _latencyOptimization = Math.Clamp(value, 0, 4);
            }

            public bool EnableStreaming
            {
                get => _enableStreaming;
                set => _enableStreaming = value;
            }

            public int MaxRetries
            {
                get => _maxRetries;
                set => _maxRetries = Math.Max(1, value);
            }

            public int RetryDelayMs
            {
                get => _retryDelayMs;
                set => _retryDelayMs = Math.Max(100, value);
            }

            protected override async void ConvertBuffer(byte[] buffer, int bitsPerSample, int sampleRate)
            {
                if (string.IsNullOrEmpty(_apiKey) || buffer.Length == 0)
                    return;

                // Add to processing queue
                using (_queueLock.EnterScope())
                {
                    _processingQueue.Enqueue([.. buffer]);
                }

                // Start processing if not already running
                if (!_isProcessing)
                    await Task.Run(ProcessQueueAsync);
            }

            private async Task ProcessQueueAsync()
            {
                await _semaphore.WaitAsync();
                _isProcessing = true;

                try
                {
                    while (true)
                    {
                        byte[]? audioData = null;

                        using (_queueLock.EnterScope())
                        {
                            if (_processingQueue.Count > 0)
                            {
                                audioData = _processingQueue.Dequeue();
                            }
                        }

                        if (audioData == null)
                            break;

                        await ProcessAudioChunkAsync(audioData);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Audio($"ElevenLabs conversion error: {ex.Message}");
                }
                finally
                {
                    _isProcessing = false;
                    _semaphore.Release();
                }
            }

            private async Task ProcessAudioChunkAsync(byte[] audioData)
            {
                for (int attempt = 0; attempt <= _maxRetries; attempt++)
                {
                    try
                    {
                        // Convert audio to the required format (16-bit PCM at 16kHz for best latency)
                        byte[] pcmData = ConvertToPcm16(audioData, 16000);

                        // Create multipart form data
                        using var formData = new MultipartFormDataContent();
                        
                        // Add audio file - using PCM format for lower latency
                        using var audioStream = new MemoryStream(pcmData);
                        formData.Add(new StreamContent(audioStream), "audio", "input.pcm");

                        // Build query parameters
                        var queryParams = new List<string>
                        {
                            $"model_id={Uri.EscapeDataString(_modelId)}",
                            $"output_format=mp3_44100_128",
                            $"file_format=pcm_s16le_16"
                        };

                        // Add optional parameters
                        if (_latencyOptimization > 0)
                        {
                            queryParams.Add($"optimize_streaming_latency={_latencyOptimization}");
                        }

                        // Add voice settings if they differ from defaults
                        var voiceSettings = new
                        {
                            stability = _stability,
                            similarity_boost = _similarityBoost,
                            style = _style,
                            use_speaker_boost = _useSpeakerBoost
                        };

                        var voiceSettingsJson = System.Text.Json.JsonSerializer.Serialize(voiceSettings);
                        formData.Add(new StringContent(voiceSettingsJson), "voice_settings");

                        // Make the API call to the correct Speech-to-Speech Stream endpoint
                        var url = $"https://api.elevenlabs.io/v1/speech-to-speech/{_voiceId}/stream?{string.Join("&", queryParams)}";
                        var response = await _httpClient.PostAsync(url, formData);

                        if (response.IsSuccessStatusCode)
                        {
                            var convertedAudio = await response.Content.ReadAsByteArrayAsync();
                            
                            // Replace the original buffer with converted audio
                            if (convertedAudio.Length > 0)
                            {
                                // Convert back to the original format and replace buffer
                                ConvertFromMp3ToOriginalFormat(convertedAudio, audioData);
                            }
                            
                            return; // Success, exit retry loop
                        }
                        else
                        {
                            var errorContent = await response.Content.ReadAsStringAsync();
                            Debug.Audio($"ElevenLabs API error: {response.StatusCode} - {errorContent}");
                            
                            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                            {
                                // Rate limited, wait longer
                                await Task.Delay(_retryDelayMs * (attempt + 1));
                            }
                            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                            {
                                Debug.Audio("ElevenLabs API key is invalid");
                                return; // Don't retry auth errors
                            }
                            else if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
                            {
                                Debug.Audio("ElevenLabs API: Unprocessable Entity - check audio format and parameters");
                                return; // Don't retry format errors
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.Audio($"ElevenLabs conversion attempt {attempt + 1} failed: {ex.Message}");
                        
                        if (attempt < _maxRetries)
                        {
                            await Task.Delay(_retryDelayMs);
                        }
                    }
                }
            }

            private static byte[] ConvertToPcm16(byte[] audioData, int targetSampleRate)
            {
                try
                {
                    // Convert audio data to 16-bit PCM at target sample rate
                    using var memoryStream = new MemoryStream();
                    
                    // Create wave format for target
                    var targetFormat = new NAudio.Wave.WaveFormat(targetSampleRate, 16, 1);
                    
                    // Convert audio data to the target format
                    byte[] convertedData;
                    
                    if (audioData.Length > 0)
                    {
                        // For now, use simple conversion - in production you'd want proper resampling
                        // This is a simplified version - you might want to use NAudio's resampling
                        convertedData = ResampleAudio(audioData, targetSampleRate, 16);
                    }
                    else
                    {
                        convertedData = new byte[targetSampleRate * 2]; // 1 second of silence at 16-bit
                    }

                    // Write PCM data directly (no WAV header for lower latency)
                    memoryStream.Write(convertedData, 0, convertedData.Length);
                    return memoryStream.ToArray();
                }
                catch (Exception ex)
                {
                    Debug.Audio($"Error converting to PCM16: {ex.Message}");
                    // Fallback to original data
                    return audioData;
                }
            }

            private static byte[] ResampleAudio(byte[] audioData, int targetSampleRate, int targetBitsPerSample)
            {
                // This is a simplified resampling function
                // In a production environment, you'd want to use proper audio resampling
                // For now, we'll do a basic conversion
                
                if (audioData.Length == 0)
                    return new byte[targetSampleRate * targetBitsPerSample / 8];

                // Simple conversion - this is not proper resampling but works for testing
                // In reality, you'd want to use NAudio's resampling capabilities
                var targetLength = targetSampleRate * targetBitsPerSample / 8;
                var result = new byte[targetLength];
                
                // Copy what we can, fill the rest with silence
                var copyLength = Math.Min(audioData.Length, targetLength);
                Buffer.BlockCopy(audioData, 0, result, 0, copyLength);
                
                return result;
            }

            private static void ConvertFromMp3ToOriginalFormat(byte[] mp3Data, byte[] originalBuffer)
            {
                try
                {
                    // Convert MP3 back to the original format
                    using var mp3Stream = new MemoryStream(mp3Data);
                    using var mp3Reader = new Mp3FileReader(mp3Stream);
                    
                    // Read the converted audio
                    var convertedSamples = new byte[mp3Reader.Length];
                    mp3Reader.ReadExactly(convertedSamples);

                    // Resize and copy to original buffer
                    if (convertedSamples.Length <= originalBuffer.Length)
                    {
                        Buffer.BlockCopy(convertedSamples, 0, originalBuffer, 0, convertedSamples.Length);
                        
                        // Fill remaining space with silence if needed
                        if (convertedSamples.Length < originalBuffer.Length)
                        {
                            for (int i = convertedSamples.Length; i < originalBuffer.Length; i++)
                            {
                                originalBuffer[i] = 0;
                            }
                        }
                    }
                    else
                    {
                        // Truncate if converted audio is longer
                        Buffer.BlockCopy(convertedSamples, 0, originalBuffer, 0, originalBuffer.Length);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Audio($"Error converting MP3 to original format: {ex.Message}");
                }
            }

            public void ClearQueue()
            {
                using (_queueLock.EnterScope())
                {
                    _processingQueue.Clear();
                }
            }

            public int GetQueueCount()
            {
                using (_queueLock.EnterScope())
                {
                    return _processingQueue.Count;
                }
            }

            public async Task WaitForCompletionAsync()
            {
                while (true)
                {
                    int queueCount;
                    using (_queueLock.EnterScope())
                    {
                        queueCount = _processingQueue.Count;
                    }

                    if (queueCount == 0 && !_isProcessing)
                        break;

                    await Task.Delay(10);
                }
            }
        }
    }
}
