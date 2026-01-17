using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Components
{
    /// <summary>
    /// Text-to-speech provider using OpenAI's TTS API.
    /// </summary>
    public class OpenAITTSProvider : ITTSProvider
    {
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;
        private readonly string _defaultModel;
        private readonly string _defaultVoice;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Available OpenAI TTS voices.
        /// </summary>
        public static readonly string[] AvailableVoices = ["alloy", "echo", "fable", "onyx", "nova", "shimmer"];

        /// <summary>
        /// Creates a new OpenAI TTS provider.
        /// </summary>
        /// <param name="apiKey">OpenAI API key.</param>
        /// <param name="model">Model to use: "tts-1" (faster) or "tts-1-hd" (higher quality).</param>
        /// <param name="defaultVoice">Default voice to use.</param>
        public OpenAITTSProvider(string apiKey, string model = "tts-1", string defaultVoice = "alloy")
        {
            _apiKey = apiKey;
            _defaultModel = model;
            _defaultVoice = defaultVoice;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        public async Task<TTSResult> SynthesizeAsync(string text, string? voice = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new OpenAITTSRequest
                {
                    Model = _defaultModel,
                    Input = text,
                    Voice = voice ?? _defaultVoice,
                    ResponseFormat = "pcm" // Raw PCM audio
                };

                var json = JsonSerializer.Serialize(request, JsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(
                    "https://api.openai.com/v1/audio/speech",
                    content,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    return TTSResult.Failure($"HTTP {response.StatusCode}: {errorText}");
                }

                var audioData = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                // OpenAI returns 24kHz mono 16-bit PCM when using "pcm" format
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

        public Task<TTSVoice[]> GetAvailableVoicesAsync(CancellationToken cancellationToken = default)
        {
            // OpenAI doesn't have an API endpoint for listing voices, so we return the known list
            var voices = new TTSVoice[]
            {
                new() { Id = "alloy", Name = "Alloy", LanguageCode = "en", Gender = EVoiceGender.Neutral, Description = "Neutral, balanced voice" },
                new() { Id = "echo", Name = "Echo", LanguageCode = "en", Gender = EVoiceGender.Male, Description = "Deep, resonant voice" },
                new() { Id = "fable", Name = "Fable", LanguageCode = "en", Gender = EVoiceGender.Neutral, Description = "Expressive, narrative voice" },
                new() { Id = "onyx", Name = "Onyx", LanguageCode = "en", Gender = EVoiceGender.Male, Description = "Deep, authoritative voice" },
                new() { Id = "nova", Name = "Nova", LanguageCode = "en", Gender = EVoiceGender.Female, Description = "Warm, friendly voice" },
                new() { Id = "shimmer", Name = "Shimmer", LanguageCode = "en", Gender = EVoiceGender.Female, Description = "Clear, optimistic voice" }
            };
            return Task.FromResult(voices);
        }

        private class OpenAITTSRequest
        {
            public string Model { get; set; } = "tts-1";
            public string Input { get; set; } = string.Empty;
            public string Voice { get; set; } = "alloy";
            public string ResponseFormat { get; set; } = "pcm";
            public double? Speed { get; set; }
        }
    }
}
