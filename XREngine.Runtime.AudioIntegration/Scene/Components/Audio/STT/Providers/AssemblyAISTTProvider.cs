using System.Text;
using System.Text.Json;

namespace XREngine.Components
{
    public class AssemblyAISTTProvider : ISTTProvider
    {
        private readonly string _apiKey;
        private readonly string _language;
        private readonly HttpClient _httpClient;

        public AssemblyAISTTProvider(string apiKey, string language)
        {
            _apiKey = apiKey;
            _language = language;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", _apiKey);
        }

        public async Task<STTResult> TranscribeAsync(byte[] audioData, int sampleRate, int bitsPerSample)
        {
            try
            {
                var content = new ByteArrayContent(audioData);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");

                var response = await _httpClient.PostAsync(
                    "https://api.assemblyai.com/v2/upload",
                    content);

                if (!response.IsSuccessStatusCode)
                {
                    return new STTResult
                    {
                        Success = false,
                        Error = $"Upload failed: HTTP {response.StatusCode}"
                    };
                }

                var uploadResponse = JsonSerializer.Deserialize<AssemblyAIUploadResponse>(await response.Content.ReadAsStringAsync());
                if (uploadResponse?.UploadUrl == null)
                {
                    return new STTResult { Success = false, Error = "Failed to get upload URL" };
                }

                // Upload the audio file
                var uploadContent = new ByteArrayContent(audioData);
                var uploadResult = await _httpClient.PutAsync(uploadResponse.UploadUrl, uploadContent);

                if (!uploadResult.IsSuccessStatusCode)
                {
                    return new STTResult { Success = false, Error = "Failed to upload audio file" };
                }

                // Start transcription
                var transcriptionRequest = new
                {
                    audio_url = uploadResponse.UploadUrl,
                    language_code = _language
                };

                var transcriptionContent = new StringContent(JsonSerializer.Serialize(transcriptionRequest), Encoding.UTF8, "application/json");
                var transcriptionResponse = await _httpClient.PostAsync("https://api.assemblyai.com/v2/transcript", transcriptionContent);

                if (!transcriptionResponse.IsSuccessStatusCode)
                {
                    return new STTResult { Success = false, Error = "Failed to start transcription" };
                }

                var transcriptionResult = JsonSerializer.Deserialize<AssemblyAITranscriptionResponse>(await transcriptionResponse.Content.ReadAsStringAsync());
                if (transcriptionResult?.Id == null)
                {
                    return new STTResult { Success = false, Error = "Failed to get transcription ID" };
                }

                // Poll for results
                for (int i = 0; i < 30; i++) // Max 30 attempts
                {
                    await Task.Delay(1000); // Wait 1 second

                    var statusResponse = await _httpClient.GetAsync($"https://api.assemblyai.com/v2/transcript/{transcriptionResult.Id}");
                    if (!statusResponse.IsSuccessStatusCode)
                        continue;

                    var statusResult = JsonSerializer.Deserialize<AssemblyAITranscriptionStatus>(await statusResponse.Content.ReadAsStringAsync());

                    if (statusResult?.Status == "completed")
                    {
                        return new STTResult
                        {
                            Success = true,
                            Text = statusResult.Text ?? "",
                            Confidence = statusResult.Confidence ?? 1.0f,
                            IsFinal = true
                        };
                    }
                    else if (statusResult?.Status == "error")
                    {
                        return new STTResult { Success = false, Error = statusResult.Error ?? "Transcription failed" };
                    }
                }

                return new STTResult { Success = false, Error = "Transcription timeout" };
            }
            catch (Exception ex)
            {
                return new STTResult { Success = false, Error = ex.Message };
            }
        }

        private class AssemblyAIUploadResponse
        {
            public string? UploadUrl { get; set; }
        }

        private class AssemblyAITranscriptionResponse
        {
            public string? Id { get; set; }
        }

        private class AssemblyAITranscriptionStatus
        {
            public string? Status { get; set; }
            public string? Text { get; set; }
            public float? Confidence { get; set; }
            public string? Error { get; set; }
        }
    }
} 