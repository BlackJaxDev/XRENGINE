using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

internal sealed class TwitchHlsStreamResolver : IHlsStreamResolver
{
    private enum TwitchSourceKind
    {
        None,
        LiveChannel,
        Vod
    }

    private const string TwitchClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private const string PlaybackAccessTokenSha256 = "0828119ded1c13477966434e15800ff57ddacf13ba1911c129dc2200705b0712";
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly HttpClient Http = new();

    public async Task<ResolvedStream> ResolveAsync(string source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Stream source cannot be empty.", nameof(source));

        string trimmed = source.Trim();
        TwitchSourceKind sourceKind = ParseTwitchSource(trimmed, out string? channel, out string? vodId);
        if (sourceKind == TwitchSourceKind.None)
        {
            var (resolvedUrl, variants) = await SelectGenericPlaylistIfNeededAsync(trimmed, cancellationToken).ConfigureAwait(false);
            Debug.Out($"Stream resolver: non-Twitch URL resolved to: '{resolvedUrl}'");
            return new ResolvedStream
            {
                Url = resolvedUrl,
                RetryCount = 0,
                OpenOptions = BuildOpenOptionsForGenericUrl(resolvedUrl),
                AvailableQualities = variants
            };
        }

        string target = sourceKind == TwitchSourceKind.Vod ? $"VOD '{vodId}'" : $"channel '{channel}'";
        Debug.Out($"Stream resolver: Twitch {sourceKind} detected — {target}");

        if (sourceKind == TwitchSourceKind.LiveChannel && string.IsNullOrWhiteSpace(channel))
            throw new InvalidOperationException("Twitch channel resolution produced an empty channel name.");

        if (sourceKind == TwitchSourceKind.Vod && string.IsNullOrWhiteSpace(vodId))
            throw new InvalidOperationException("Twitch VOD resolution produced an empty VOD id.");

        int retryCount = 0;
        (string token, string signature) = await RequestPlaybackTokenWithRetryAsync(sourceKind, channel, vodId, cancellationToken, retries => retryCount += retries).ConfigureAwait(false);
        Debug.Out($"Stream resolver: Twitch token acquired (sig={signature[..Math.Min(8, signature.Length)]}...)");

        string masterPlaylistUrl = sourceKind == TwitchSourceKind.Vod
            ? BuildVodMasterPlaylistUrl(vodId!, token, signature)
            : BuildLiveMasterPlaylistUrl(channel!, token, signature);
        var (selectedUrl, twitchVariants) = await SelectBestPlaylistWithRetryAsync(masterPlaylistUrl, cancellationToken, retries => retryCount += retries).ConfigureAwait(false);
        Debug.Out($"Stream resolver: Twitch variant selected: '{selectedUrl}'");

        return new ResolvedStream
        {
            Url = selectedUrl,
            RetryCount = retryCount,
            OpenOptions = BuildOpenOptionsForTwitch(),
            AvailableQualities = twitchVariants
        };
    }

    private static async Task<(string token, string signature)> RequestPlaybackTokenWithRetryAsync(
        TwitchSourceKind sourceKind,
        string? channel,
        string? vodId,
        CancellationToken cancellationToken,
        Action<int> onRetries)
    {
        const int maxAttempts = 4;
        int retries = 0;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql");
                request.Headers.Add("Client-ID", TwitchClientId);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));

                var payload = new
                {
                    operationName = "PlaybackAccessToken_Template",
                    query = "query PlaybackAccessToken_Template($login: String!, $isLive: Boolean!, $vodID: ID!, $isVod: Boolean!, $playerType: String!) {  streamPlaybackAccessToken(channelName: $login, params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: $playerType}) @include(if: $isLive) {    value    signature    __typename  }  videoPlaybackAccessToken(id: $vodID, params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: $playerType}) @include(if: $isVod) {    value    signature    __typename  }}",
                    variables = new
                    {
                        isLive = sourceKind == TwitchSourceKind.LiveChannel,
                        login = sourceKind == TwitchSourceKind.LiveChannel ? channel : string.Empty,
                        isVod = sourceKind == TwitchSourceKind.Vod,
                        vodID = sourceKind == TwitchSourceKind.Vod ? vodId : string.Empty,
                        playerType = "site"
                    }
                };

                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                string responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(responseJson);

                JsonElement data = doc.RootElement.GetProperty("data");
                JsonElement tokenData;
                if (data.TryGetProperty("streamPlaybackAccessToken", out tokenData) ||
                    data.TryGetProperty("videoPlaybackAccessToken", out tokenData))
                {
                }
                else
                {
                    throw new InvalidOperationException("Twitch token response did not include a playback access token payload.");
                }

                if (tokenData.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    string target = sourceKind == TwitchSourceKind.Vod
                        ? $"VOD '{vodId}'"
                        : $"channel '{channel}'";
                    throw new InvalidOperationException($"Twitch playback token was not available for {target}.");
                }

                string? token = tokenData.GetProperty("value").GetString();
                string? signature = tokenData.GetProperty("signature").GetString();

                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(signature))
                    throw new InvalidOperationException("Twitch token response did not include a valid token/signature.");

                onRetries(retries);
                return (token, signature);
            }
            catch when (attempt < maxAttempts)
            {
                retries++;
                TimeSpan backoff = ComputeBackoff(attempt);
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Failed to resolve Twitch token for channel '{channel}' after {maxAttempts} attempts.");
    }

    private static async Task<(string selectedUrl, IReadOnlyList<StreamVariantInfo> variants)> SelectBestPlaylistWithRetryAsync(
        string masterPlaylistUrl,
        CancellationToken cancellationToken,
        Action<int> onRetries)
    {
        const int maxAttempts = 4;
        int retries = 0;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string playlist = await Http.GetStringAsync(masterPlaylistUrl, cancellationToken).ConfigureAwait(false);
                var variants = StreamVariantInfo.ParseFromMasterPlaylist(masterPlaylistUrl, playlist);
                string bestVariant = variants.Count > 0 ? variants[0].Url : SelectBestVariant(masterPlaylistUrl, playlist);
                onRetries(retries);
                return (bestVariant, variants);
            }
            catch when (attempt < maxAttempts)
            {
                retries++;
                TimeSpan backoff = ComputeBackoff(attempt);
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Failed to fetch Twitch playlist '{masterPlaylistUrl}' after {maxAttempts} attempts.");
    }

    private static string SelectBestVariant(string masterPlaylistUrl, string playlist)
    {
        string[] lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return masterPlaylistUrl;

        int bestBandwidth = -1;
        string? bestUrl = null;

        for (int i = 0; i < lines.Length - 1; i++)
        {
            string line = lines[i];
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                continue;

            string next = lines[i + 1];
            if (string.IsNullOrWhiteSpace(next) || next.StartsWith('#'))
                continue;

            int bandwidth = ParseBandwidth(line);
            if (bandwidth < bestBandwidth)
                continue;

            bestBandwidth = bandwidth;
            bestUrl = MakeAbsolute(masterPlaylistUrl, next);
        }

        return string.IsNullOrWhiteSpace(bestUrl) ? masterPlaylistUrl : bestUrl;
    }

    private static int ParseBandwidth(string extInfLine)
    {
        const string key = "BANDWIDTH=";
        int start = extInfLine.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return -1;

        start += key.Length;
        int end = extInfLine.IndexOf(',', start);
        string value = end >= 0 ? extInfLine[start..end] : extInfLine[start..];
        return int.TryParse(value, out int bandwidth) ? bandwidth : -1;
    }

    private static string MakeAbsolute(string baseUrl, string maybeRelative)
    {
        if (Uri.TryCreate(maybeRelative, UriKind.Absolute, out Uri? absolute))
            return absolute.ToString();

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
            return maybeRelative;

        return new Uri(baseUri, maybeRelative).ToString();
    }

    private static string BuildLiveMasterPlaylistUrl(string channel, string token, string signature)
        => $"https://usher.ttvnw.net/api/channel/hls/{channel}.m3u8" +
           "?player=twitchweb" +
           "&platform=web" +
           $"&p={RandomNumberGenerator.GetInt32(100000, 999999)}" +
           $"&token={Uri.EscapeDataString(token)}" +
           $"&sig={signature}" +
           "&allow_source=true" +
           "&allow_audio_only=true" +
           "&playlist_include_framerate=true" +
           "&supported_codecs=av1,h265,h264" +
           "&fast_bread=true";

    private static string BuildVodMasterPlaylistUrl(string vodId, string token, string signature)
        => $"https://usher.ttvnw.net/vod/{vodId}.m3u8" +
           "?player=twitchweb" +
           $"&nauth={Uri.EscapeDataString(token)}" +
           $"&nauthsig={signature}" +
           "&allow_source=true" +
           "&allow_audio_only=true" +
           "&fast_bread=true";

    private static StreamOpenOptions BuildOpenOptionsForTwitch()
        => new()
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
            Referrer = "https://player.twitch.tv",
            Headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
                ["Accept"] = "*/*",
                ["Origin"] = "https://player.twitch.tv",
                ["Referer"] = "https://player.twitch.tv"
            }
        };

    private static StreamOpenOptions BuildOpenOptionsForGenericUrl(string source)
    {
        if (source.Contains("ttvnw.net", StringComparison.OrdinalIgnoreCase) || source.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase))
            return BuildOpenOptionsForTwitch();

        return new StreamOpenOptions();
    }

    private static async Task<(string selectedUrl, IReadOnlyList<StreamVariantInfo> variants)> SelectGenericPlaylistIfNeededAsync(string source, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
            return (source, []);

        string path = uri.AbsolutePath;
        bool likelyHls = path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                         source.Contains("m3u8", StringComparison.OrdinalIgnoreCase);
        if (!likelyHls)
            return (source, []);

        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string playlist = await Http.GetStringAsync(source, cancellationToken).ConfigureAwait(false);
                var variants = StreamVariantInfo.ParseFromMasterPlaylist(source, playlist);
                if (variants.Count > 0)
                {
                    string selected = variants[0].Url;
                    Debug.Out($"Stream resolver: generic master playlist -> selected variant '{selected}'");
                    return (selected, variants);
                }

                // No STREAM-INF found — this is already a media playlist.
                return (source, []);
            }
            catch when (attempt < maxAttempts)
            {
                await Task.Delay(ComputeBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        Debug.Out($"Stream resolver: generic HLS variant selection failed, using original URL '{source}'");
        return (source, []);
    }

    private static TwitchSourceKind ParseTwitchSource(string source, out string? channel, out string? vodId)
    {
        channel = null;
        vodId = null;

        if (source.StartsWith("twitch:", StringComparison.OrdinalIgnoreCase))
        {
            channel = NormalizeChannel(source["twitch:".Length..]);
            return string.IsNullOrWhiteSpace(channel) ? TwitchSourceKind.None : TwitchSourceKind.LiveChannel;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            string host = uri.Host.ToLowerInvariant();
            if (!host.Contains("twitch.tv") && !host.Contains("ttvnw.net"))
                return TwitchSourceKind.None;

            if (host.Contains("ttvnw.net"))
            {
                vodId = TryExtractVodIdFromPath(uri.AbsolutePath);
                return string.IsNullOrWhiteSpace(vodId) ? TwitchSourceKind.None : TwitchSourceKind.Vod;
            }

            string[] segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length >= 2 &&
                segments[0].Equals("videos", StringComparison.OrdinalIgnoreCase))
            {
                vodId = NormalizeVodId(segments[1]);
                return string.IsNullOrWhiteSpace(vodId) ? TwitchSourceKind.None : TwitchSourceKind.Vod;
            }

            string firstSegment = segments.FirstOrDefault() ?? string.Empty;

            channel = NormalizeChannel(firstSegment);
            return string.IsNullOrWhiteSpace(channel) ? TwitchSourceKind.None : TwitchSourceKind.LiveChannel;
        }

        string? inlineVodId = TryExtractVodIdFromPath(source);
        if (!string.IsNullOrWhiteSpace(inlineVodId))
        {
            vodId = inlineVodId;
            return TwitchSourceKind.Vod;
        }

        string normalized = NormalizeChannel(source);
        if (string.IsNullOrWhiteSpace(normalized))
            return TwitchSourceKind.None;

        channel = normalized;
        return TwitchSourceKind.LiveChannel;
    }

    private static string? TryExtractVodIdFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string trimmed = path.Trim();

        if (trimmed.StartsWith("/videos/", StringComparison.OrdinalIgnoreCase))
        {
            string segment = trimmed["/videos/".Length..].Split('/', '?', '#')[0];
            return NormalizeVodId(segment);
        }

        if (trimmed.StartsWith("/vod/", StringComparison.OrdinalIgnoreCase))
        {
            string segment = trimmed["/vod/".Length..].Split('/', '?', '#')[0];
            return NormalizeVodId(segment);
        }

        return null;
    }

    private static string NormalizeVodId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string trimmed = value.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[1..];

        return trimmed.All(char.IsDigit) ? trimmed : string.Empty;
    }

    private static string NormalizeChannel(string value)
    {
        string trimmed = value.Trim().TrimStart('@').Trim('/');
        if (trimmed.Length == 0)
            return string.Empty;

        return trimmed;
    }

    private static TimeSpan ComputeBackoff(int attempt)
    {
        int multiplier = 1 << Math.Min(attempt - 1, 4);
        double milliseconds = InitialBackoff.TotalMilliseconds * multiplier;
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, 4000));
    }
}
