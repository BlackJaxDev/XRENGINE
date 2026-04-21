using System.Text.Json;

namespace XREngine.Components
{
    public class RevAISTTProvider : ISTTProvider
    {
        private readonly string _apiKey;
        private readonly string _language;
        private readonly HttpClient _httpClient;

        public RevAISTTProvider(string apiKey, string language)
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
                formData.Add(new StringContent(_language), "language");

                var response = await _httpClient.PostAsync("https://api.rev.ai/api/v1/jobs", formData);

                if (!response.IsSuccessStatusCode)
                {
                    return new STTResult
                    {
                        Success = false,
                        Error = $"HTTP {response.StatusCode}: {await response.Content.ReadAsStringAsync()}"
                    };
                }

                var jobResponse = JsonSerializer.Deserialize<RevAIJobResponse>(await response.Content.ReadAsStringAsync());
                if (jobResponse?.Id == null)
                {
                    return new STTResult { Success = false, Error = "Failed to get job ID" };
                }

                // Poll for completion
                for (int i = 0; i < 60; i++) // Max 60 attempts
                {
                    await Task.Delay(2000); // Wait 2 seconds

                    var statusResponse = await _httpClient.GetAsync($"https://api.rev.ai/api/v1/jobs/{jobResponse.Id}");
                    if (!statusResponse.IsSuccessStatusCode)
                        continue;

                    var statusResult = JsonSerializer.Deserialize<RevAIJobStatus>(await statusResponse.Content.ReadAsStringAsync());

                    if (statusResult?.Status == "transcribed")
                    {
                        // Get transcript
                        var transcriptResponse = await _httpClient.GetAsync($"https://api.rev.ai/api/v1/jobs/{jobResponse.Id}/transcript");
                        if (!transcriptResponse.IsSuccessStatusCode)
                            continue;

                        var transcriptResult = JsonSerializer.Deserialize<RevAITranscript>(await transcriptResponse.Content.ReadAsStringAsync());

                        return new STTResult
                        {
                            Success = true,
                            Text = transcriptResult?.Monologues?.FirstOrDefault()?.Elements?.FirstOrDefault()?.Value ?? "",
                            Confidence = 1.0f, // Rev.ai doesn't provide confidence scores in this format
                            IsFinal = true
                        };
                    }
                    else if (statusResult?.Status == "failed")
                    {
                        return new STTResult { Success = false, Error = "Transcription failed" };
                    }
                }

                return new STTResult { Success = false, Error = "Transcription timeout" };
            }
            catch (Exception ex)
            {
                return new STTResult { Success = false, Error = ex.Message };
            }
        }

        private class RevAIJobResponse
        {
            public string? Id { get; set; }
        }

        private class RevAIJobStatus
        {
            public string? Status { get; set; }
        }

        private class RevAITranscript
        {
            public RevAIMonologue[]? Monologues { get; set; }
        }

        private class RevAIMonologue
        {
            public RevAIElement[]? Elements { get; set; }
        }

        private class RevAIElement
        {
            public string? Value { get; set; }
        }
    }
} 