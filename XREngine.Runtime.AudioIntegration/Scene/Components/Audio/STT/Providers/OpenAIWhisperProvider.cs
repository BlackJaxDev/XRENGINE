using System.Text.Json;

namespace XREngine.Components
{
    public class OpenAIWhisperProvider : ISTTProvider
    {
        private readonly string _apiKey;
        private readonly string _language;
        private readonly HttpClient _httpClient;

        public OpenAIWhisperProvider(string apiKey, string language)
        {
            _apiKey = apiKey;
            _language = language;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        public async Task<STTResult> TranscribeAsync(byte[] audioData, int sampleRate, int bitsPerSample)
        {
            try
            {
                using var formData = new MultipartFormDataContent();
                using var audioStream = new MemoryStream(audioData);
                formData.Add(new StreamContent(audioStream), "file", "audio.wav");
                formData.Add(new StringContent("whisper-1"), "model");
                formData.Add(new StringContent(_language), "language");

                var response = await _httpClient.PostAsync("https://api.openai.com/v1/audio/transcriptions", formData);

                if (!response.IsSuccessStatusCode)
                {
                    return new STTResult
                    {
                        Success = false,
                        Error = $"HTTP {response.StatusCode}: {await response.Content.ReadAsStringAsync()}"
                    };
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<OpenAIWhisperResponse>(responseJson);

                return new STTResult
                {
                    Success = true,
                    Text = result?.Text ?? "",
                    Confidence = 1.0f, // OpenAI doesn't provide confidence scores
                    IsFinal = true
                };
            }
            catch (Exception ex)
            {
                return new STTResult { Success = false, Error = ex.Message };
            }
        }

        private class OpenAIWhisperResponse
        {
            public string? Text { get; set; }
        }
    }
} 