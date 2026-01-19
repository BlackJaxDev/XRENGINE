using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Components
{
    /// <summary>
    /// Text-to-speech provider using Google Cloud Text-to-Speech API.
    /// </summary>
    /// <remarks>
    /// Creates a new Google Cloud TTS provider.
    /// </remarks>
    /// <param name="apiKey">Google Cloud API key.</param>
    /// <param name="defaultLanguage">Default language code (e.g., "en-US").</param>
    public class GoogleTTSProvider(string apiKey, string defaultLanguage = "en-US") : ITTSProvider
    {
        private readonly HttpClient _httpClient = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public async Task<TTSResult> SynthesizeAsync(string text, string? voice = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new GoogleTTSRequest
                {
                    Input = new GoogleTTSInput { Text = text },
                    Voice = new GoogleTTSVoice
                    {
                        LanguageCode = defaultLanguage,
                        Name = voice ?? $"{defaultLanguage}-Standard-A"
                    },
                    AudioConfig = new GoogleTTSAudioConfig
                    {
                        AudioEncoding = "LINEAR16",
                        SampleRateHertz = 24000
                    }
                };

                var json = JsonSerializer.Serialize(request, JsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(
                    $"https://texttospeech.googleapis.com/v1/text:synthesize?key={apiKey}",
                    content,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    return TTSResult.Failure($"HTTP {response.StatusCode}: {errorText}");
                }

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<GoogleTTSResponse>(responseJson, JsonOptions);

                if (string.IsNullOrEmpty(result?.AudioContent))
                {
                    return TTSResult.Failure("No audio content in response");
                }

                // Google returns base64-encoded audio
                var audioData = Convert.FromBase64String(result.AudioContent);

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
                var response = await _httpClient.GetAsync(
                    $"https://texttospeech.googleapis.com/v1/voices?key={apiKey}",
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return [];
                }

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<GoogleVoicesResponse>(responseJson, JsonOptions);

                return result?.Voices?.Select(v => new TTSVoice
                {
                    Id = v.Name ?? string.Empty,
                    Name = v.Name ?? string.Empty,
                    LanguageCode = v.LanguageCodes?.FirstOrDefault() ?? string.Empty,
                    Gender = v.SsmlGender?.ToUpperInvariant() switch
                    {
                        "MALE" => EVoiceGender.Male,
                        "FEMALE" => EVoiceGender.Female,
                        _ => EVoiceGender.Neutral
                    }
                }).ToArray() ?? [];
            }
            catch
            {
                return [];
            }
        }

        #region Request/Response Types

        private class GoogleTTSRequest
        {
            public GoogleTTSInput? Input { get; set; }
            public GoogleTTSVoice? Voice { get; set; }
            public GoogleTTSAudioConfig? AudioConfig { get; set; }
        }

        private class GoogleTTSInput
        {
            public string? Text { get; set; }
            public string? Ssml { get; set; }
        }

        private class GoogleTTSVoice
        {
            public string? LanguageCode { get; set; }
            public string? Name { get; set; }
            public string? SsmlGender { get; set; }
        }

        private class GoogleTTSAudioConfig
        {
            public string AudioEncoding { get; set; } = "LINEAR16";
            public int SampleRateHertz { get; set; } = 24000;
            public double? SpeakingRate { get; set; }
            public double? Pitch { get; set; }
            public double? VolumeGainDb { get; set; }
        }

        private class GoogleTTSResponse
        {
            public string? AudioContent { get; set; }
        }

        private class GoogleVoicesResponse
        {
            public GoogleVoiceInfo[]? Voices { get; set; }
        }

        private class GoogleVoiceInfo
        {
            public string[]? LanguageCodes { get; set; }
            public string? Name { get; set; }
            public string? SsmlGender { get; set; }
            public int? NaturalSampleRateHertz { get; set; }
        }

        #endregion
    }
}
