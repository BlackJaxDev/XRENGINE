using System.Text.Json;

namespace XREngine.Components
{
    public class DeepgramSTTProvider : ISTTProvider
    {
        private readonly string _apiKey;
        private readonly string _language;
        private readonly HttpClient _httpClient;

        public DeepgramSTTProvider(string apiKey, string language)
        {
            _apiKey = apiKey;
            _language = language;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Token {_apiKey}");
        }

        public async Task<STTResult> TranscribeAsync(byte[] audioData, int sampleRate, int bitsPerSample)
        {
            try
            {
                var content = new ByteArrayContent(audioData);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");

                var response = await _httpClient.PostAsync(
                    $"https://api.deepgram.com/v1/listen?model=nova-2&language={_language}&punctuate=true",
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
                var result = JsonSerializer.Deserialize<DeepgramSTTResponse>(responseJson);

                if (result?.Results?.Channels?.Length > 0 && result.Results.Channels[0].Alternatives?.Length > 0)
                {
                    var transcript = result.Results.Channels[0].Alternatives[0];
                    return new STTResult
                    {
                        Success = true,
                        Text = transcript.Transcript ?? "",
                        Confidence = transcript.Confidence ?? 1.0f,
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

        private class DeepgramSTTResponse
        {
            public DeepgramSTTResults? Results { get; set; }
        }

        private class DeepgramSTTResults
        {
            public DeepgramSTTChannel[]? Channels { get; set; }
        }

        private class DeepgramSTTChannel
        {
            public DeepgramSTTAlternative[]? Alternatives { get; set; }
        }

        private class DeepgramSTTAlternative
        {
            public string? Transcript { get; set; }
            public float? Confidence { get; set; }
        }
    }
} 