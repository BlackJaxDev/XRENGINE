using System.Text.Json;

namespace XREngine.Components
{
    public class AzureSTTProvider : ISTTProvider
    {
        private readonly string _apiKey;
        private readonly string _language;
        private readonly HttpClient _httpClient;
        private readonly string _region = "eastus"; // Default region

        public AzureSTTProvider(string apiKey, string language)
        {
            _apiKey = apiKey;
            _language = language;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _apiKey);
        }

        public async Task<STTResult> TranscribeAsync(byte[] audioData, int sampleRate, int bitsPerSample)
        {
            try
            {
                var content = new ByteArrayContent(audioData);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");

                var response = await _httpClient.PostAsync(
                    $"https://{_region}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1?language={_language}",
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
                var result = JsonSerializer.Deserialize<AzureSTTResponse>(responseJson);

                return new STTResult
                {
                    Success = result?.RecognitionStatus == "Success",
                    Text = result?.DisplayText ?? "",
                    Confidence = 1.0f, // Azure doesn't provide confidence in this endpoint
                    IsFinal = true
                };
            }
            catch (Exception ex)
            {
                return new STTResult { Success = false, Error = ex.Message };
            }
        }

        private class AzureSTTResponse
        {
            public string? RecognitionStatus { get; set; }
            public string? DisplayText { get; set; }
        }
    }
} 