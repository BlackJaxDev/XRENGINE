using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Components
{
    /// <summary>
    /// Text-to-speech provider using Amazon Polly via REST API.
    /// </summary>
    /// <remarks>
    /// Creates a new Amazon Polly TTS provider.
    /// </remarks>
    /// <param name="accessKeyId">AWS Access Key ID.</param>
    /// <param name="secretAccessKey">AWS Secret Access Key.</param>
    /// <param name="region">AWS region (e.g., "us-east-1").</param>
    /// <param name="engine">Voice engine: "standard" or "neural".</param>
    public class AmazonPollyTTSProvider(string accessKeyId, string secretAccessKey, string region = "us-east-1", string engine = "neural") : ITTSProvider
    {
        private readonly HttpClient _httpClient = new();

        public async Task<TTSResult> SynthesizeAsync(string text, string? voice = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var voiceId = voice ?? "Joanna";
                var endpoint = $"https://polly.{region}.amazonaws.com/v1/speech";
                
                var requestBody = new
                {
                    Engine = engine,
                    OutputFormat = "pcm",
                    SampleRate = "24000",
                    Text = text,
                    TextType = "text",
                    VoiceId = voiceId
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Create AWS Signature V4 signed request
                var request = await CreateSignedRequestAsync(HttpMethod.Post, endpoint, json, cancellationToken);
                request.Content = content;

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

        public Task<TTSVoice[]> GetAvailableVoicesAsync(CancellationToken cancellationToken = default)
        {
            // Return a subset of common Polly voices
            // Full list requires AWS SDK or signed API call
            var voices = new TTSVoice[]
            {
                new() { Id = "Joanna", Name = "Joanna", LanguageCode = "en-US", Gender = EVoiceGender.Female },
                new() { Id = "Matthew", Name = "Matthew", LanguageCode = "en-US", Gender = EVoiceGender.Male },
                new() { Id = "Ivy", Name = "Ivy", LanguageCode = "en-US", Gender = EVoiceGender.Female, Description = "Child voice" },
                new() { Id = "Kendra", Name = "Kendra", LanguageCode = "en-US", Gender = EVoiceGender.Female },
                new() { Id = "Kimberly", Name = "Kimberly", LanguageCode = "en-US", Gender = EVoiceGender.Female },
                new() { Id = "Salli", Name = "Salli", LanguageCode = "en-US", Gender = EVoiceGender.Female },
                new() { Id = "Joey", Name = "Joey", LanguageCode = "en-US", Gender = EVoiceGender.Male },
                new() { Id = "Justin", Name = "Justin", LanguageCode = "en-US", Gender = EVoiceGender.Male, Description = "Child voice" },
                new() { Id = "Amy", Name = "Amy", LanguageCode = "en-GB", Gender = EVoiceGender.Female },
                new() { Id = "Brian", Name = "Brian", LanguageCode = "en-GB", Gender = EVoiceGender.Male },
                new() { Id = "Emma", Name = "Emma", LanguageCode = "en-GB", Gender = EVoiceGender.Female },
            };
            return Task.FromResult(voices);
        }

        private async Task<HttpRequestMessage> CreateSignedRequestAsync(HttpMethod method, string url, string body, CancellationToken cancellationToken)
        {
            // Simplified AWS Signature V4 implementation
            // For production, consider using AWS SDK
            var uri = new Uri(url);
            var host = uri.Host;
            var amzDate = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var dateStamp = DateTime.UtcNow.ToString("yyyyMMdd");

            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("Host", host);
            request.Headers.Add("X-Amz-Date", amzDate);

            // Create canonical request
            var contentHash = ComputeSha256Hash(body);
            request.Headers.Add("X-Amz-Content-Sha256", contentHash);

            var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{contentHash}\nx-amz-date:{amzDate}\n";
            var signedHeaders = "host;x-amz-content-sha256;x-amz-date";

            var canonicalRequest = $"{method}\n{uri.AbsolutePath}\n\n{canonicalHeaders}\n{signedHeaders}\n{contentHash}";
            var canonicalRequestHash = ComputeSha256Hash(canonicalRequest);

            // Create string to sign
            var algorithm = "AWS4-HMAC-SHA256";
            var credentialScope = $"{dateStamp}/{region}/polly/aws4_request";
            var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{canonicalRequestHash}";

            // Calculate signature
            var signingKey = GetSignatureKey(secretAccessKey, dateStamp, region, "polly");
            var signature = ComputeHmacSha256(signingKey, stringToSign);
            var signatureHex = BitConverter.ToString(signature).Replace("-", "").ToLowerInvariant();

            // Create authorization header
            var authorization = $"{algorithm} Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signatureHex}";
            request.Headers.Add("Authorization", authorization);

            return request;
        }

        private static string ComputeSha256Hash(string input)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexStringLower(bytes);
        }

        private static byte[] ComputeHmacSha256(byte[] key, string data)
        {
            using var hmac = new System.Security.Cryptography.HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
        {
            var kDate = ComputeHmacSha256(Encoding.UTF8.GetBytes("AWS4" + key), dateStamp);
            var kRegion = ComputeHmacSha256(kDate, regionName);
            var kService = ComputeHmacSha256(kRegion, serviceName);
            var kSigning = ComputeHmacSha256(kService, "aws4_request");
            return kSigning;
        }
    }
}
