using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Components
{
    /// <summary>
    /// Text-to-speech provider using ElevenLabs API.
    /// Known for high-quality, expressive voice synthesis.
    /// </summary>
    public class ElevenLabsTTSProvider : ITTSProvider
    {
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;
        private readonly string _defaultVoiceId;
        private readonly string _modelId;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Creates a new ElevenLabs TTS provider.
        /// </summary>
        /// <param name="apiKey">ElevenLabs API key.</param>
        /// <param name="defaultVoiceId">Default voice ID to use.</param>
        /// <param name="modelId">Model ID: "eleven_monolingual_v1", "eleven_multilingual_v2", etc.</param>
        public ElevenLabsTTSProvider(string apiKey, string defaultVoiceId = "21m00Tcm4TlvDq8ikWAM", string modelId = "eleven_monolingual_v1")
        {
            _apiKey = apiKey;
            _defaultVoiceId = defaultVoiceId;
            _modelId = modelId;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("xi-api-key", _apiKey);
        }

        public async Task<TTSResult> SynthesizeAsync(string text, string? voice = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var voiceId = voice ?? _defaultVoiceId;

                var request = new ElevenLabsRequest
                {
                    Text = text,
                    ModelId = _modelId,
                    VoiceSettings = new ElevenLabsVoiceSettings
                    {
                        Stability = 0.5,
                        SimilarityBoost = 0.75
                    }
                };

                var json = JsonSerializer.Serialize(request, JsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Request PCM format via query parameter
                var response = await _httpClient.PostAsync(
                    $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}?output_format=pcm_24000",
                    content,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    return TTSResult.Failure($"HTTP {response.StatusCode}: {errorText}");
                }

                var audioData = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                // ElevenLabs PCM is 24kHz mono 16-bit
                return TTSResult.FromPcm(audioData, sampleRate: 24000, channels: 1, bitsPerSample: 16);
            }
            catch (OperationCanceledException)
            {
                return TTSResult.Failure("Operation cancelled");
            }
            catch (Exception ex)
            {
                return TTSResult.Failure(ex.Message);
            }
        }

        public async Task<TTSVoice[]> GetAvailableVoicesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync("https://api.elevenlabs.io/v1/voices", cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return [];
                }

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<ElevenLabsVoicesResponse>(responseJson, JsonOptions);

                return result?.Voices?.Select(v => new TTSVoice
                {
                    Id = v.VoiceId ?? string.Empty,
                    Name = v.Name ?? string.Empty,
                    LanguageCode = "en", // ElevenLabs is primarily English-focused
                    Gender = EVoiceGender.Neutral, // ElevenLabs doesn't expose gender in API
                    Description = v.Description
                }).ToArray() ?? [];
            }
            catch
            {
                return [];
            }
        }

        #region Request/Response Types

        private class ElevenLabsRequest
        {
            public string Text { get; set; } = string.Empty;
            public string ModelId { get; set; } = "eleven_monolingual_v1";
            public ElevenLabsVoiceSettings? VoiceSettings { get; set; }
        }

        private class ElevenLabsVoiceSettings
        {
            public double Stability { get; set; } = 0.5;
            public double SimilarityBoost { get; set; } = 0.75;
            public double? Style { get; set; }
            public bool? UseSpeakerBoost { get; set; }
        }

        private class ElevenLabsVoicesResponse
        {
            public ElevenLabsVoiceInfo[]? Voices { get; set; }
        }

        private class ElevenLabsVoiceInfo
        {
            public string? VoiceId { get; set; }
            public string? Name { get; set; }
            public string? Category { get; set; }
            public string? Description { get; set; }
            public ElevenLabsVoiceLabels? Labels { get; set; }
        }

        private class ElevenLabsVoiceLabels
        {
            public string? Accent { get; set; }
            public string? Description { get; set; }
            public string? Age { get; set; }
            public string? Gender { get; set; }
            public string? UseCase { get; set; }
        }

        #endregion
    }
}
