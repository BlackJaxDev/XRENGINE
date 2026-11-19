using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// Reads current API model catalogs for the Assistant menus. Credentials stay in
/// request headers; provider response bodies are not included in diagnostics.
/// </summary>
internal static class AssistantModelCatalog
{
    private const int MaximumPages = 32;
    private const int MaximumModels = 4096;
    private const int MaximumPageBytes = 2 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Reads every catalog page within bounded time and memory. The caller must
    /// discard the result if the selected connection changes during the request.
    /// </summary>
    public static async Task<IReadOnlyList<AssistantModelOption>> LoadAsync(
        AssistantCatalogProvider provider,
        string credential,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(provider))
            throw new ArgumentOutOfRangeException(nameof(provider));
        if (string.IsNullOrWhiteSpace(credential))
            throw new InvalidOperationException("Connect an API account to load its models.");
        if (credential.Length > 16_384 || credential.Trim().Any(char.IsControl))
            throw new InvalidOperationException("The API credential format is invalid. Check the saved connection.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var models = new Dictionary<string, AssistantModelOption>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            for (int page = 0; page < MaximumPages; page++)
            {
                using HttpRequestMessage request = CreateRequest(provider, credential.Trim(), cursor);
                using HttpResponseMessage response = await Client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw CreateResponseError(response.StatusCode);

                using JsonDocument document = await ReadPageAsync(response.Content, timeout.Token).ConfigureAwait(false);
                JsonElement root = document.RootElement;
                string collectionName = provider == AssistantCatalogProvider.Gemini ? "models" : "data";
                if (!root.TryGetProperty(collectionName, out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The provider returned an invalid model list.");

                foreach (JsonElement item in items.EnumerateArray())
                {
                    AssistantModelOption? model = ReadModel(provider, item);
                    if (model is null)
                        continue;
                    models[model.Id] = model;
                    if (models.Count > MaximumModels)
                        throw new InvalidDataException("The provider model list exceeded the supported size.");
                }

                cursor = ReadNextCursor(provider, root);
                if (string.IsNullOrEmpty(cursor))
                {
                    return models.Values
                        .OrderBy(static model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(static model => model.Id, StringComparer.Ordinal)
                        .ToArray();
                }
                if (cursor.Length > 2048 || !cursors.Add(cursor))
                    throw new InvalidDataException("The provider returned an invalid model-list page cursor.");
            }
            throw new InvalidDataException("The provider model list exceeded the supported page count.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Model discovery timed out. Try again.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Could not reach the model service. Check the connection and try again.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The provider returned an invalid model list.");
        }
    }

    private static HttpRequestMessage CreateRequest(AssistantCatalogProvider provider, string credential, string? cursor)
    {
        string url = provider switch
        {
            AssistantCatalogProvider.OpenAi => "https://api.openai.com/v1/models",
            AssistantCatalogProvider.Claude => "https://api.anthropic.com/v1/models?limit=100",
            AssistantCatalogProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/models?pageSize=100",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        if (cursor is not null)
        {
            string parameter = provider == AssistantCatalogProvider.Claude ? "after_id" : "pageToken";
            url += $"&{parameter}={Uri.EscapeDataString(cursor)}";
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (provider == AssistantCatalogProvider.Claude)
        {
            request.Headers.Add("x-api-key", credential);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else if (provider == AssistantCatalogProvider.Gemini)
            request.Headers.Add("x-goog-api-key", credential);
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private static async Task<JsonDocument> ReadPageAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumPageBytes)
            throw new InvalidDataException("The provider model-list page exceeded the supported size.");
        using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] block = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(block, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumPageBytes)
                throw new InvalidDataException("The provider model-list page exceeded the supported size.");
            buffer.Write(block, 0, count);
        }
        return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)),
            new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static AssistantModelOption? ReadModel(AssistantCatalogProvider provider, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        string? id = ReadString(item, provider == AssistantCatalogProvider.Gemini ? "name" : "id");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 512)
            return null;
        bool realtime = provider == AssistantCatalogProvider.OpenAi
            && id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
            && id.Contains("realtime", StringComparison.OrdinalIgnoreCase);
        if (provider == AssistantCatalogProvider.Gemini)
        {
            if (!item.TryGetProperty("supportedGenerationMethods", out JsonElement methods)
                || methods.ValueKind != JsonValueKind.Array
                || !methods.EnumerateArray().Any(static method => method.ValueKind == JsonValueKind.String && method.GetString() == "generateContent"))
                return null;
            if (!id.StartsWith("models/", StringComparison.Ordinal))
                return null;
            id = id[7..];
            if (!id.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) || IsSpecializedModel(id))
                return null;
        }
        else if (provider == AssistantCatalogProvider.OpenAi && !realtime && !IsOpenAiTextModel(id))
            return null;
        else if (provider == AssistantCatalogProvider.Claude && ReadString(item, "lifecycle") == "retired")
            return null;

        string? displayName = ReadString(item, provider == AssistantCatalogProvider.Gemini ? "displayName" : "display_name");
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 512)
            displayName = id;
        string capacityName = provider switch
        {
            AssistantCatalogProvider.Claude => "max_input_tokens",
            AssistantCatalogProvider.Gemini => "inputTokenLimit",
            _ => "context_window",
        };
        int? capacity = item.TryGetProperty(capacityName, out JsonElement capacityValue)
            && capacityValue.ValueKind == JsonValueKind.Number
            && capacityValue.TryGetInt32(out int tokens) && tokens > 0 ? tokens : null;
        return new AssistantModelOption(id, displayName, capacity, realtime);
    }

    private static bool IsOpenAiTextModel(string id)
    {
        if (IsSpecializedModel(id))
            return false;
        ReadOnlySpan<char> baseId = id.AsSpan();
        if (baseId.StartsWith("ft:", StringComparison.OrdinalIgnoreCase))
            baseId = baseId[3..];
        // The API catalog does not report endpoint capabilities. Keep the chat
        // families accepted by this panel and omit specialized output models.
        return baseId.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
            || baseId.StartsWith("chatgpt-", StringComparison.OrdinalIgnoreCase)
            || baseId.StartsWith("codex-", StringComparison.OrdinalIgnoreCase)
            || (baseId.Length > 1 && (baseId[0] is 'o' or 'O') && char.IsAsciiDigit(baseId[1]));
    }

    private static bool IsSpecializedModel(string id)
        => id.Contains("embedding", StringComparison.OrdinalIgnoreCase)
            || id.Contains("instruct", StringComparison.OrdinalIgnoreCase)
            || id.Contains("moderation", StringComparison.OrdinalIgnoreCase)
            || id.Contains("transcrib", StringComparison.OrdinalIgnoreCase)
            || id.Contains("tts", StringComparison.OrdinalIgnoreCase)
            || id.Contains("realtime", StringComparison.OrdinalIgnoreCase)
            || id.Contains("audio", StringComparison.OrdinalIgnoreCase)
            || id.Contains("image", StringComparison.OrdinalIgnoreCase)
            || id.Contains("deep-research", StringComparison.OrdinalIgnoreCase)
            || id.Contains("search", StringComparison.OrdinalIgnoreCase);

    private static string? ReadNextCursor(AssistantCatalogProvider provider, JsonElement root)
    {
        if (provider == AssistantCatalogProvider.Gemini)
            return ReadString(root, "nextPageToken");
        if (provider != AssistantCatalogProvider.Claude
            || !root.TryGetProperty("has_more", out JsonElement hasMore)
            || hasMore.ValueKind != JsonValueKind.True)
            return null;
        string? cursor = ReadString(root, "last_id");
        return !string.IsNullOrWhiteSpace(cursor) ? cursor
            : throw new InvalidDataException("The provider omitted a required model-list page cursor.");
    }

    private static string? ReadString(JsonElement item, string property)
        => item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static InvalidOperationException CreateResponseError(HttpStatusCode status)
        => new(status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The account could not load models. Check its API credentials and access.",
            HttpStatusCode.TooManyRequests => "The model service is busy or its request limit was reached. Try again later.",
            _ => $"The model service could not load models (HTTP {(int)status}). Try again later.",
        });
}
