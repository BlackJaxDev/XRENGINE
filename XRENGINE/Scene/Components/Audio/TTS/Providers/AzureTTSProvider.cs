using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Components
{
    /// <summary>
    /// Text-to-speech provider using Azure Cognitive Services Speech API.
    /// </summary>
    /// <remarks>
    /// Creates a new Azure TTS provider.
    /// </remarks>
    /// <param name="apiKey">Azure Speech API subscription key.</param>
    /// <param name="region">Azure region (e.g., "eastus", "westeurope").</param>
    /// <param name="defaultLanguage">Default language code (e.g., "en-US").</param>
    public class AzureTTSProvider(string apiKey, string region, string defaultLanguage = "en-US") : ITTSProvider
    {
        private readonly HttpClient _httpClient = new HttpClient();
        private string? _accessToken;
        private DateTime _tokenExpiry = DateTime.MinValue;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public async Task<TTSResult> SynthesizeAsync(string text, string? voice = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureTokenAsync(cancellationToken);

                var voiceName = voice ?? $"{defaultLanguage}-JennyNeural";
                var ssml = $@"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{defaultLanguage}'>
                    <voice name='{voiceName}'>{EscapeXml(text)}</voice>
                </speak>";

                using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Headers.Add("X-Microsoft-OutputFormat", "raw-24khz-16bit-mono-pcm");
                request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    return TTSResult.Failure($"HTTP {response.StatusCode}: {errorText}");
                }

                var audioData = await response.Content.ReadAsByteArrayAsync(cancellationToken);

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
                await EnsureTokenAsync(cancellationToken);

                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return [];
                }

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var voices = JsonSerializer.Deserialize<AzureVoiceInfo[]>(responseJson, JsonOptions);

                return voices?.Select(v => new TTSVoice
                {
                    Id = v.ShortName ?? string.Empty,
                    Name = v.DisplayName ?? v.LocalName ?? v.ShortName ?? string.Empty,
                    LanguageCode = v.Locale ?? string.Empty,
                    Gender = v.Gender?.ToUpperInvariant() switch
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

        private async Task EnsureTokenAsync(CancellationToken cancellationToken)
        {
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiry)
                return;

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://{region}.api.cognitive.microsoft.com/sts/v1.0/issueToken");
            request.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            _accessToken = await response.Content.ReadAsStringAsync(cancellationToken);
            _tokenExpiry = DateTime.UtcNow.AddMinutes(9); // Token valid for 10 minutes, refresh at 9
        }

        private static string EscapeXml(string text)
        {
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private class AzureVoiceInfo
        {
            public string? Name { get; set; }
            public string? DisplayName { get; set; }
            public string? LocalName { get; set; }
            public string? ShortName { get; set; }
            public string? Gender { get; set; }
            public string? Locale { get; set; }
            public string? LocaleName { get; set; }
            public string? VoiceType { get; set; }
            public string[]? StyleList { get; set; }
        }
    }
}
