using System.Text;
using System.Text.Json;

namespace XREngine.Components
{
    public class GoogleSTTProvider(string apiKey, string language) : ISTTProvider
    {
        private readonly HttpClient _httpClient = new();

        public async Task<STTResult> TranscribeAsync(byte[] audioData, int sampleRate, int bitsPerSample)
        {
            try
            {
                // Convert audio to base64
                string audioContent = Convert.ToBase64String(audioData);

                var request = new
                {
                    config = new
                    {
                        encoding = bitsPerSample == 16 ? "LINEAR16" : "LINEAR8",
                        sampleRateHertz = sampleRate,
                        languageCode = language,
                        enableAutomaticPunctuation = true,
                        enableWordTimeOffsets = false
                    },
                    audio = new
                    {
                        content = audioContent
                    }
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(
                    $"https://speech.googleapis.com/v1/speech:recognize?key={apiKey}",
                    content);

                if (!response.IsSuccessStatusCode)
                {
                    return new STTResult
                    {
                        Success = false,
                        Error = $"HTTP {response.StatusCode}: {await response.Content.ReadAsStringAsync()}"
                    };
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<GoogleSTTResponse>(responseJson);

                if (result?.Results?.Length > 0)
                {
                    var transcript = result.Results[0].Alternatives[0];
                    return new STTResult
                    {
                        Success = true,
                        Text = transcript.Transcript ?? string.Empty,
                        Confidence = transcript.Confidence,
                        IsFinal = true
                    };
                }

                return new STTResult { Success = false, Error = "No transcription results" };
            }
            catch (Exception ex)
            {
                return new STTResult { Success = false, Error = ex.Message };
            }
        }

        private class GoogleSTTResponse
        {
            public GoogleSTTResult[]? Results { get; set; }
        }

        private class GoogleSTTResult
        {
            public GoogleSTTAlternative[]? Alternatives { get; set; }
        }

        private class GoogleSTTAlternative
        {
            public string? Transcript { get; set; }
            public float Confidence { get; set; }
        }
    }
} 