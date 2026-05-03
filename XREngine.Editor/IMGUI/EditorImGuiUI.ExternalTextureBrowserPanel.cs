using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ImGuiNET;
using Newtonsoft.Json.Linq;
using XREngine.Diagnostics;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const float ExternalTextureTileBaseEdge = 164.0f;
    private const float ExternalTextureTileMinEdge = 96.0f;
    private const float ExternalTextureTileMaxEdge = 280.0f;
    private const float ExternalTextureTilePadding = 8.0f;
    private const string ExternalTextureBrowserDefaultRelativeOutput = "Textures/External";
    private static readonly HttpClient _externalTextureHttpClient = CreateExternalTextureHttpClient();
    private static readonly object _externalTextureStateLock = new();
    private static readonly List<ExternalTextureCatalogItem> _externalTextureCatalog = [];
    private static readonly Dictionary<string, ExternalTexturePreviewState> _externalTexturePreviewCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ExternalTextureDownloadState> _externalTextureDownloadStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly RegexOptions ExternalTextureRegexOptions = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;
    private static ExternalTextureSourceFilter _externalTextureSourceFilter = ExternalTextureSourceFilter.All;
    private static ExternalTextureSortMode _externalTextureSortMode = ExternalTextureSortMode.Name;
    private static string _externalTextureSearch = string.Empty;
    private static string _externalTextureOutputRelativePath = ExternalTextureBrowserDefaultRelativeOutput;
    private static float _externalTextureTileScale = 1.0f;
    private static bool _showExternalTextureBrowser;
    private static bool _externalTextureCatalogRefreshInFlight;
    private static string? _externalTextureCatalogStatus;
    private static string? _externalTextureCatalogError;
    private static DateTime _externalTextureCatalogUpdatedUtc;

    private enum ExternalTextureSourceFilter
    {
        All,
        PixelFurnace,
        FreePbr,
    }

    private enum ExternalTextureSortMode
    {
        Name,
        Source,
        Category,
        Newest,
    }

    private enum ExternalTextureSourceKind
    {
        PixelFurnace,
        FreePbr,
    }

    private sealed record ExternalTextureCatalogItem(
        ExternalTextureSourceKind Source,
        string Id,
        string Name,
        string DetailUrl,
        string DownloadUrl,
        string PreviewUrl,
        string Category,
        string Resolution,
        string FileSize,
        DateTime PublishedUtc)
    {
        public string SourceLabel => Source == ExternalTextureSourceKind.PixelFurnace ? "Pixel-Furnace" : "FreePBR";
        public string StableKey => $"{Source}:{Id}";
    }

    private sealed class ExternalTexturePreviewState
    {
        public string PreviewUrl { get; init; } = string.Empty;
        public string CachePath { get; init; } = string.Empty;
        public XRTexture2D? Texture { get; set; }
        public bool DownloadInFlight { get; set; }
        public bool PreviewInFlight { get; set; }
        public string? Error { get; set; }
        public uint RequestedSize { get; set; }
    }

    private sealed class ExternalTextureDownloadState
    {
        public bool InFlight { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Error { get; set; }
        public string? ExtractedPath { get; set; }
    }

    private sealed class PixelFurnaceDownloadDescriptor
    {
        public string Url { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
    }

    private sealed class FreePbrDownloadDescriptor
    {
        public string PostUrl { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public Dictionary<string, string> Fields { get; init; } = new(StringComparer.Ordinal);
    }

    private static HttpClient CreateExternalTextureHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = true,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XRENGINE External Texture Browser");
        return client;
    }

    private static void DrawExternalTextureBrowserPanel()
    {
        if (!_showExternalTextureBrowser)
            return;

        if (!ImGui.Begin("External Textures", ref _showExternalTextureBrowser, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        using var profilerScope = Engine.Profiler.Start("UI.DrawExternalTextureBrowserPanel");
        DrawExternalTextureBrowserMenuBar();
        DrawExternalTextureBrowserToolbar();

        List<ExternalTextureCatalogItem> visibleItems = GetFilteredExternalTextureItems();
        DrawExternalTextureBrowserStatus(visibleItems.Count);
        ImGui.Separator();

        if (_externalTextureCatalogRefreshInFlight && visibleItems.Count == 0)
        {
            ImGui.TextDisabled("Refreshing catalog...");
        }
        else if (visibleItems.Count == 0)
        {
            ImGui.TextDisabled("No texture packs match the current filter.");
        }
        else
        {
            DrawExternalTextureGrid(visibleItems);
        }

        ImGui.End();
    }

    private static void DrawExternalTextureBrowserMenuBar()
    {
        if (!ImGui.BeginMenuBar())
            return;

        if (ImGui.MenuItem("Refresh", null, false, !_externalTextureCatalogRefreshInFlight))
            RefreshExternalTextureCatalog();

        if (ImGui.MenuItem("Open Download Folder"))
            OpenExternalTextureDownloadFolder();

        ImGui.EndMenuBar();
    }

    private static void DrawExternalTextureBrowserToolbar()
    {
        if (_externalTextureCatalog.Count == 0 && !_externalTextureCatalogRefreshInFlight)
            RefreshExternalTextureCatalog();

        ImGui.SetNextItemWidth(150.0f);
        if (ImGui.BeginCombo("##ExternalTextureSource", GetExternalTextureSourceFilterLabel(_externalTextureSourceFilter)))
        {
            DrawExternalTextureSourceFilterOption(ExternalTextureSourceFilter.All);
            DrawExternalTextureSourceFilterOption(ExternalTextureSourceFilter.PixelFurnace);
            DrawExternalTextureSourceFilterOption(ExternalTextureSourceFilter.FreePbr);
            ImGui.EndCombo();
        }

        ImGui.SameLine(0f, 8f);
        ImGui.SetNextItemWidth(260.0f);
        ImGui.InputTextWithHint("##ExternalTextureSearch", "Search packs...", ref _externalTextureSearch, 128u);

        ImGui.SameLine(0f, 8f);
        ImGui.SetNextItemWidth(140.0f);
        if (ImGui.BeginCombo("##ExternalTextureSort", GetExternalTextureSortLabel(_externalTextureSortMode)))
        {
            DrawExternalTextureSortOption(ExternalTextureSortMode.Name);
            DrawExternalTextureSortOption(ExternalTextureSortMode.Source);
            DrawExternalTextureSortOption(ExternalTextureSortMode.Category);
            DrawExternalTextureSortOption(ExternalTextureSortMode.Newest);
            ImGui.EndCombo();
        }

        ImGui.SameLine(0f, 8f);
        ImGui.SetNextItemWidth(120.0f);
        if (ImGui.SliderFloat("Size##ExternalTextureTileSize", ref _externalTextureTileScale, 0.65f, 1.7f, "%.2fx"))
            _externalTextureTileScale = Math.Clamp(_externalTextureTileScale, 0.65f, 1.7f);

        ImGui.SetNextItemWidth(Math.Max(280.0f, ImGui.GetContentRegionAvail().X * 0.55f));
        ImGui.InputTextWithHint("Output##ExternalTextureOutput", ExternalTextureBrowserDefaultRelativeOutput, ref _externalTextureOutputRelativePath, 260u);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _externalTextureOutputRelativePath = NormalizeExternalTextureRelativeOutput(_externalTextureOutputRelativePath);
    }

    private static void DrawExternalTextureSourceFilterOption(ExternalTextureSourceFilter filter)
    {
        bool selected = _externalTextureSourceFilter == filter;
        if (ImGui.Selectable(GetExternalTextureSourceFilterLabel(filter), selected))
            _externalTextureSourceFilter = filter;
        if (selected)
            ImGui.SetItemDefaultFocus();
    }

    private static void DrawExternalTextureSortOption(ExternalTextureSortMode mode)
    {
        bool selected = _externalTextureSortMode == mode;
        if (ImGui.Selectable(GetExternalTextureSortLabel(mode), selected))
            _externalTextureSortMode = mode;
        if (selected)
            ImGui.SetItemDefaultFocus();
    }

    private static string GetExternalTextureSourceFilterLabel(ExternalTextureSourceFilter filter)
        => filter switch
        {
            ExternalTextureSourceFilter.PixelFurnace => "Pixel-Furnace",
            ExternalTextureSourceFilter.FreePbr => "FreePBR",
            _ => "All Sources",
        };

    private static string GetExternalTextureSortLabel(ExternalTextureSortMode mode)
        => mode switch
        {
            ExternalTextureSortMode.Source => "Source",
            ExternalTextureSortMode.Category => "Category",
            ExternalTextureSortMode.Newest => "Newest",
            _ => "Name",
        };

    private static void DrawExternalTextureBrowserStatus(int visibleCount)
    {
        string status;
        int totalCount;
        lock (_externalTextureStateLock)
        {
            totalCount = _externalTextureCatalog.Count;
            status = _externalTextureCatalogStatus ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(status))
            ImGui.TextDisabled(status);
        else if (totalCount > 0)
            ImGui.TextDisabled($"{visibleCount} visible / {totalCount} cataloged");

        if (_externalTextureCatalogUpdatedUtc != default)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_externalTextureCatalogUpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        string? error = _externalTextureCatalogError;
        if (!string.IsNullOrWhiteSpace(error))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.42f, 0.32f, 1.0f), error);
        }
    }

    private static List<ExternalTextureCatalogItem> GetFilteredExternalTextureItems()
    {
        List<ExternalTextureCatalogItem> items;
        lock (_externalTextureStateLock)
            items = [.. _externalTextureCatalog];

        string search = _externalTextureSearch.Trim();
        if (_externalTextureSourceFilter != ExternalTextureSourceFilter.All)
        {
            ExternalTextureSourceKind kind = _externalTextureSourceFilter == ExternalTextureSourceFilter.PixelFurnace
                ? ExternalTextureSourceKind.PixelFurnace
                : ExternalTextureSourceKind.FreePbr;
            items.RemoveAll(item => item.Source != kind);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            items.RemoveAll(item =>
                !item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !item.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !item.Resolution.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !item.SourceLabel.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        Comparison<ExternalTextureCatalogItem> comparison = _externalTextureSortMode switch
        {
            ExternalTextureSortMode.Source => static (left, right) =>
            {
                int source = string.Compare(left.SourceLabel, right.SourceLabel, StringComparison.OrdinalIgnoreCase);
                return source != 0 ? source : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            },
            ExternalTextureSortMode.Category => static (left, right) =>
            {
                int category = string.Compare(left.Category, right.Category, StringComparison.OrdinalIgnoreCase);
                return category != 0 ? category : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            },
            ExternalTextureSortMode.Newest => static (left, right) =>
            {
                int date = right.PublishedUtc.CompareTo(left.PublishedUtc);
                return date != 0 ? date : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            },
            _ => static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase),
        };
        items.Sort(comparison);
        return items;
    }

    private static void DrawExternalTextureGrid(IReadOnlyList<ExternalTextureCatalogItem> items)
    {
        float previewEdge = Math.Clamp(ExternalTextureTileBaseEdge * _externalTextureTileScale, ExternalTextureTileMinEdge, ExternalTextureTileMaxEdge);
        float padding = ExternalTextureTilePadding;
        float tileWidth = previewEdge + padding * 2.0f;
        float labelHeight = ImGui.GetTextLineHeightWithSpacing() * 4.0f;
        float actionHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing() + padding * 0.5f;
        float tileHeight = previewEdge + padding * 2.0f + labelHeight + actionHeight;
        float availableWidth = Math.Max(1.0f, ImGui.GetContentRegionAvail().X);
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        int columns = Math.Max(1, (int)MathF.Floor((availableWidth + spacing) / (tileWidth + spacing)));
        int rowCount = (items.Count + columns - 1) / columns;
        float rowHeight = tileHeight + ImGui.GetStyle().ItemSpacing.Y;

        unsafe
        {
            var clipper = new ImGuiListClipper();
            ImGuiNative.ImGuiListClipper_Begin(&clipper, rowCount, rowHeight);
            while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
            {
                for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                {
                    int startIndex = row * columns;
                    int endIndex = Math.Min(startIndex + columns, items.Count);
                    for (int itemIndex = startIndex; itemIndex < endIndex; itemIndex++)
                    {
                        DrawExternalTextureTile(items[itemIndex], tileWidth, tileHeight, previewEdge, labelHeight, padding);
                        if (itemIndex + 1 < endIndex)
                            ImGui.SameLine(0f, spacing);
                    }
                }
            }

            ImGuiNative.ImGuiListClipper_End(&clipper);
        }
    }

    private static void DrawExternalTextureTile(ExternalTextureCatalogItem item, float tileWidth, float tileHeight, float previewEdge, float labelHeight, float padding)
    {
        ImGui.PushID(item.StableKey);
        ImGui.BeginGroup();

        Vector2 tileSize = new(tileWidth, tileHeight);
        Vector2 tilePos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##ExternalTextureTileHitbox", tileSize);
        bool hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip($"{item.SourceLabel}\n{item.Name}\n{item.DetailUrl}");

        var drawList = ImGui.GetWindowDrawList();
        uint baseColor = ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg);
        uint borderColor = ImGui.GetColorU32(ImGuiCol.Border);
        drawList.AddRectFilled(tilePos, tilePos + tileSize, baseColor, 6.0f);
        drawList.AddRect(tilePos, tilePos + tileSize, borderColor, 6.0f);

        Vector2 previewSize = new(previewEdge, previewEdge);
        Vector2 previewPos = tilePos + new Vector2(padding, padding);
        DrawExternalTexturePreview(item, previewPos, previewSize, previewEdge, drawList);

        Vector2 textPos = tilePos + new Vector2(padding, previewEdge + padding * 1.5f);
        float textWidth = tileWidth - padding * 2.0f;
        ImGui.SetCursorScreenPos(textPos);
        ImGui.PushTextWrapPos(textPos.X + textWidth);
        ImGui.TextUnformatted(item.Name);
        ImGui.TextDisabled(item.SourceLabel);
        string details = FormatExternalTextureDetails(item);
        if (!string.IsNullOrWhiteSpace(details))
            ImGui.TextDisabled(details);
        ImGui.PopTextWrapPos();

        Vector2 buttonPos = tilePos + new Vector2(padding, tileHeight - GetExternalTextureActionRowHeight());
        ImGui.SetCursorScreenPos(buttonPos);
        DrawExternalTextureDownloadButton(item, textWidth);

        ImGui.EndGroup();
        ImGui.PopID();
    }

    private static float GetExternalTextureActionRowHeight()
    {
        return ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing() + ExternalTextureTilePadding * 0.5f;
    }

    private static string FormatExternalTextureDetails(ExternalTextureCatalogItem item)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(item.Category))
            parts.Add(item.Category);
        if (!string.IsNullOrWhiteSpace(item.Resolution))
            parts.Add(item.Resolution);
        if (!string.IsNullOrWhiteSpace(item.FileSize))
            parts.Add(item.FileSize);
        return string.Join(" | ", parts);
    }

    private static void DrawExternalTexturePreview(ExternalTextureCatalogItem item, Vector2 previewPos, Vector2 previewSize, float previewEdge, ImDrawListPtr drawList)
    {
        ExternalTexturePreviewState? preview = GetOrCreateExternalTexturePreviewState(item);
        uint desiredSize = (uint)Math.Clamp(previewEdge, 64.0f, 512.0f);
        RequestExternalTexturePreview(preview, desiredSize);

        if (preview.Texture is not null && TryGetTexturePreviewHandle(preview.Texture, previewEdge, out nint handle, out Vector2 displaySize))
        {
            Vector2 imagePos = previewPos + (previewSize - displaySize) * 0.5f;
            ImGui.SetCursorScreenPos(imagePos);
            ImGui.Image(handle, displaySize);
            return;
        }

        uint fillColor = ImGui.GetColorU32(ImGuiCol.FrameBgActive);
        drawList.AddRectFilled(previewPos, previewPos + previewSize, fillColor, 4.0f);
        string label = preview.Error is null
            ? (preview.DownloadInFlight || preview.PreviewInFlight ? "Loading" : item.SourceLabel)
            : "Preview";
        Vector2 textSize = ImGui.CalcTextSize(label);
        drawList.AddText(previewPos + (previewSize - textSize) * 0.5f, ImGui.GetColorU32(ImGuiCol.Text), label);
    }

    private static void DrawExternalTextureDownloadButton(ExternalTextureCatalogItem item, float width)
    {
        ExternalTextureDownloadState state = GetOrCreateExternalTextureDownloadState(item.StableKey);
        bool downloaded = IsExternalTextureDownloaded(item, out string? existingPath);
        bool enabled = !state.InFlight;
        string label = state.InFlight ? "Downloading..." : downloaded ? "Redownload" : "Download";

        if (!enabled)
            ImGui.BeginDisabled();

        if (ImGui.Button(label, new Vector2(width, 0.0f)))
            StartExternalTextureDownload(item, force: downloaded);

        if (!enabled)
            ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(state.Error))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.42f, 0.32f, 1.0f), TrimExternalTextureStatus(state.Error, 28));
        }
        else if (!string.IsNullOrWhiteSpace(state.Status))
        {
            ImGui.TextDisabled(TrimExternalTextureStatus(state.Status, 28));
        }
        else if (downloaded && existingPath is not null)
        {
            ImGui.TextDisabled("Downloaded");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(existingPath);
        }
    }

    private static string TrimExternalTextureStatus(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 1)] + "...";

    private static void RefreshExternalTextureCatalog()
    {
        if (_externalTextureCatalogRefreshInFlight)
            return;

        _externalTextureCatalogRefreshInFlight = true;
        _externalTextureCatalogError = null;
        _externalTextureCatalogStatus = "Refreshing catalog...";

        _ = Task.Run(async () =>
        {
            try
            {
                var pixelFurnaceTask = LoadPixelFurnaceCatalogAsync();
                var freePbrTask = LoadFreePbrCatalogAsync();
                await Task.WhenAll(pixelFurnaceTask, freePbrTask).ConfigureAwait(false);

                var merged = pixelFurnaceTask.Result.Concat(freePbrTask.Result)
                    .GroupBy(item => item.StableKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

                lock (_externalTextureStateLock)
                {
                    _externalTextureCatalog.Clear();
                    _externalTextureCatalog.AddRange(merged);
                }

                _externalTextureCatalogUpdatedUtc = DateTime.UtcNow;
                _externalTextureCatalogStatus = $"{merged.Count} packs loaded";
            }
            catch (Exception ex)
            {
                _externalTextureCatalogError = ex.Message;
                _externalTextureCatalogStatus = "Catalog refresh failed";
                Debug.LogException(ex, "External texture catalog refresh failed.");
            }
            finally
            {
                _externalTextureCatalogRefreshInFlight = false;
            }
        });
    }

    private static async Task<List<ExternalTextureCatalogItem>> LoadFreePbrCatalogAsync()
    {
        const string endpoint = "https://freepbr.com/wp-json/wc/store/v1/products";
        var results = new List<ExternalTextureCatalogItem>();

        for (int page = 1; page <= 16; page++)
        {
            string url = $"{endpoint}?per_page=100&page={page}&orderby=date&order=desc";
            string json = await _externalTextureHttpClient.GetStringAsync(url).ConfigureAwait(false);
            var products = JArray.Parse(json);
            if (products.Count == 0)
                break;

            foreach (JToken product in products)
            {
                string priceRaw = product.SelectToken("prices.price")?.Value<string>() ?? string.Empty;
                if (!string.Equals(priceRaw, "0", StringComparison.Ordinal))
                    continue;

                string id = product.Value<string>("id") ?? string.Empty;
                string name = HtmlDecode(product.Value<string>("name"));
                string slug = product.Value<string>("slug") ?? id;
                string detailUrl = product.Value<string>("permalink") ?? string.Empty;
                string previewUrl = product.SelectToken("images[0].src")?.Value<string>() ?? string.Empty;
                string category = string.Join(", ", product["categories"]?.Select(c => HtmlDecode(c.Value<string>("name")))?.Where(s => !string.IsNullOrWhiteSpace(s)) ?? []);
                string description = PlainText(product.Value<string>("short_description") ?? product.Value<string>("description") ?? string.Empty);
                string resolution = ExtractResolution(description);
                DateTime publishedUtc = ExtractWordPressDate(product) ?? DateTime.MinValue;

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(detailUrl))
                    continue;

                results.Add(new ExternalTextureCatalogItem(
                    ExternalTextureSourceKind.FreePbr,
                    id.Length == 0 ? slug : id,
                    name,
                    detailUrl,
                    string.Empty,
                    previewUrl,
                    category,
                    resolution,
                    string.Empty,
                    publishedUtc));
            }

            if (products.Count < 100)
                break;
        }

        return results;
    }

    private static async Task<List<ExternalTextureCatalogItem>> LoadPixelFurnaceCatalogAsync()
    {
        const string baseUrl = "https://textures.pixel-furnace.com/";
        string homepage = await _externalTextureHttpClient.GetStringAsync(baseUrl).ConfigureAwait(false);
        Dictionary<string, string> defaultFields = GetPixelFurnaceFilterDefaults(homepage);
        var results = new List<ExternalTextureCatalogItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int page = 1; page <= 32; page++)
        {
            var fields = new Dictionary<string, string>(defaultFields, StringComparer.Ordinal)
            {
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
            };

            using var content = new FormUrlEncodedContent(fields);
            using HttpResponseMessage response = await _externalTextureHttpClient.PostAsync(new Uri(new Uri(baseUrl), "fetchArticles.php"), content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            List<ExternalTextureCatalogItem> pageItems = ParsePixelFurnaceCatalogPage(html, page);
            if (pageItems.Count == 0)
                break;

            int newCount = 0;
            foreach (ExternalTextureCatalogItem item in pageItems)
            {
                if (seen.Add(item.DownloadUrl))
                {
                    results.Add(item);
                    newCount++;
                }
            }

            if (page > 1 && newCount == 0)
                break;
        }

        return results;
    }

    private static Dictionary<string, string> GetPixelFurnaceFilterDefaults(string html)
    {
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        Match formMatch = Regex.Match(html, "(?is)<form[^>]*\\bid\\s*=\\s*[\"']filter[\"'][^>]*>(?<content>.*?)</form>", ExternalTextureRegexOptions);
        if (!formMatch.Success)
            return defaults;

        string formHtml = formMatch.Groups["content"].Value;
        foreach (Match inputMatch in Regex.Matches(formHtml, "(?is)<input\\b(?<attrs>[^>]*?)>", ExternalTextureRegexOptions))
        {
            string attrs = inputMatch.Groups["attrs"].Value;
            string name = GetHtmlAttribute(attrs, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            string type = GetHtmlAttribute(attrs, "type");
            string value = GetHtmlAttribute(attrs, "value");
            bool isChecked = Regex.IsMatch(attrs, "(?i)\\bchecked\\b");
            type = string.IsNullOrWhiteSpace(type) ? "text" : type.ToLowerInvariant();

            if (type is "checkbox" or "radio")
            {
                if (isChecked)
                    defaults[name] = string.IsNullOrWhiteSpace(value) ? "on" : value;
            }
            else
            {
                defaults[name] = value ?? string.Empty;
            }
        }

        foreach (Match selectMatch in Regex.Matches(formHtml, "(?is)<select\\b(?<attrs>[^>]*?)>(?<content>.*?)</select>", ExternalTextureRegexOptions))
        {
            string name = GetHtmlAttribute(selectMatch.Groups["attrs"].Value, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            MatchCollection options = Regex.Matches(selectMatch.Groups["content"].Value, "(?is)<option\\b(?<attrs>[^>]*?)>(?<text>.*?)</option>", ExternalTextureRegexOptions);
            if (options.Count == 0)
                continue;

            Match selected = options.Cast<Match>().FirstOrDefault(option => Regex.IsMatch(option.Groups["attrs"].Value, "(?i)\\bselected\\b")) ?? options[0];
            string value = GetHtmlAttribute(selected.Groups["attrs"].Value, "value");
            defaults[name] = string.IsNullOrWhiteSpace(value) ? PlainText(selected.Groups["text"].Value) : value;
        }

        return defaults;
    }

    private static List<ExternalTextureCatalogItem> ParsePixelFurnaceCatalogPage(string html, int page)
    {
        const string sourceRoot = "https://textures.pixel-furnace.com/";
        var results = new List<ExternalTextureCatalogItem>();
        foreach (Match articleMatch in Regex.Matches(html, "(?is)<article\\b[^>]*>(?<content>.*?)</article>", ExternalTextureRegexOptions))
        {
            string card = articleMatch.Groups["content"].Value;
            Match nameMatch = Regex.Match(card, "(?is)<a[^>]*href\\s*=\\s*[\"'](?<href>[^\"']*texture\\?name=(?<nameenc>[^\"']+))[\"'][^>]*>\\s*<h2[^>]*>(?<name>[^<]+)</h2>", ExternalTextureRegexOptions);
            Match downloadMatch = Regex.Match(card, "(?is)href\\s*=\\s*[\"'](?<url>(?:https?://textures\\.pixel-furnace\\.com)?/?uploads/textures/[^\"']+\\.zip)[\"']", ExternalTextureRegexOptions);
            if (!downloadMatch.Success)
                continue;

            string downloadUrl = ResolveUrl(sourceRoot, downloadMatch.Groups["url"].Value);
            string fileName = Path.GetFileName(Uri.UnescapeDataString(new Uri(downloadUrl).AbsolutePath));
            string name = nameMatch.Success
                ? HtmlDecode(nameMatch.Groups["name"].Value.Trim())
                : Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ');
            string detailUrl = nameMatch.Success ? ResolveUrl(sourceRoot, nameMatch.Groups["href"].Value) : sourceRoot;
            string previewUrl = GetPixelFurnacePreviewUrl(card, sourceRoot);
            string resolution = string.Empty;
            Match resolutionMatch = Regex.Match(card, "(?is)<span\\s+class=[\"']number[\"']>(?<res>[^<]+)</span>px", ExternalTextureRegexOptions);
            if (resolutionMatch.Success)
                resolution = resolutionMatch.Groups["res"].Value.Trim() + "px";

            string fileSize = string.Empty;
            Match sizeMatch = Regex.Match(card, "(?is)<span\\s+class=[\"']fileSize[\"']>(?<size>[^<]+)</span>", ExternalTextureRegexOptions);
            if (sizeMatch.Success)
                fileSize = HtmlDecode(sizeMatch.Groups["size"].Value.Trim());

            string articleId = string.Empty;
            Match articleIdMatch = Regex.Match(card, "(?is)data-article\\s*=\\s*[\"'](?<id>\\d+)[\"']", ExternalTextureRegexOptions);
            if (articleIdMatch.Success)
                articleId = articleIdMatch.Groups["id"].Value;

            string id = string.IsNullOrWhiteSpace(articleId) ? downloadUrl : articleId;
            results.Add(new ExternalTextureCatalogItem(
                ExternalTextureSourceKind.PixelFurnace,
                id,
                name,
                detailUrl,
                downloadUrl,
                previewUrl,
                string.Empty,
                resolution,
                fileSize,
                DateTime.MinValue.AddDays(page)));
        }

        return results;
    }

    private static string GetPixelFurnacePreviewUrl(string card, string sourceRoot)
    {
        Match dataSrcMatch = Regex.Match(card, "(?is)<img\\b[^>]*\\bdata-src\\s*=\\s*[\"'](?<url>[^\"']+)[\"']", ExternalTextureRegexOptions);
        if (dataSrcMatch.Success)
            return ResolveUrl(sourceRoot, dataSrcMatch.Groups["url"].Value);

        Match hrefMatch = Regex.Match(card, "(?is)<a\\b[^>]*href\\s*=\\s*[\"'](?<url>(?:https?://textures\\.pixel-furnace\\.com)?/?uploads/renders/[^\"']+)[\"']", ExternalTextureRegexOptions);
        return hrefMatch.Success ? ResolveUrl(sourceRoot, hrefMatch.Groups["url"].Value) : string.Empty;
    }

    private static ExternalTexturePreviewState GetOrCreateExternalTexturePreviewState(ExternalTextureCatalogItem item)
    {
        string key = string.IsNullOrWhiteSpace(item.PreviewUrl) ? item.StableKey : item.PreviewUrl;
        lock (_externalTexturePreviewCache)
        {
            if (_externalTexturePreviewCache.TryGetValue(key, out ExternalTexturePreviewState? state))
                return state;

            string cachePath = Path.Combine(GetExternalTexturePreviewCacheDirectory(), $"{ComputeStableFileName(key)}{GetPreviewCacheExtension(item.PreviewUrl)}");
            state = new ExternalTexturePreviewState
            {
                PreviewUrl = item.PreviewUrl,
                CachePath = cachePath,
            };
            _externalTexturePreviewCache[key] = state;
            return state;
        }
    }

    private static void RequestExternalTexturePreview(ExternalTexturePreviewState state, uint desiredSize)
    {
        if (string.IsNullOrWhiteSpace(state.PreviewUrl))
        {
            state.Error ??= "No preview URL.";
            return;
        }

        if (!File.Exists(state.CachePath))
        {
            if (state.DownloadInFlight)
                return;

            state.DownloadInFlight = true;
            state.Error = null;
            _ = Task.Run(async () =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(state.CachePath)!);
                    byte[] bytes = await _externalTextureHttpClient.GetByteArrayAsync(state.PreviewUrl).ConfigureAwait(false);
                    string tempPath = state.CachePath + ".download";
                    await File.WriteAllBytesAsync(tempPath, bytes).ConfigureAwait(false);
                    File.Move(tempPath, state.CachePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    state.Error = ex.Message;
                }
                finally
                {
                    state.DownloadInFlight = false;
                }
            });
            return;
        }

        if (state.PreviewInFlight || state.RequestedSize >= desiredSize)
            return;

        state.RequestedSize = Math.Max(state.RequestedSize, desiredSize);
        state.PreviewInFlight = true;
        XRTexture2D seed = state.Texture ?? new XRTexture2D
        {
            FilePath = state.CachePath,
            Name = Path.GetFileNameWithoutExtension(state.CachePath),
        };

        XRTexture2D.SchedulePreviewJob(
            state.CachePath,
            seed,
            desiredSize,
            onFinished: tex => Engine.InvokeOnMainThread(() =>
            {
                state.Texture = tex;
                state.PreviewInFlight = false;
            }, "External Textures: Preview ready"),
            onError: ex => Engine.InvokeOnMainThread(() =>
            {
                state.Error = ex.Message;
                state.PreviewInFlight = false;
                Debug.LogException(ex, $"External texture preview failed for '{state.CachePath}'.");
            }, "External Textures: Preview failed"),
            onCanceled: () => Engine.InvokeOnMainThread(() =>
            {
                state.PreviewInFlight = false;
            }, "External Textures: Preview canceled"));
    }

    private static ExternalTextureDownloadState GetOrCreateExternalTextureDownloadState(string key)
    {
        lock (_externalTextureDownloadStates)
        {
            if (_externalTextureDownloadStates.TryGetValue(key, out ExternalTextureDownloadState? state))
                return state;

            state = new ExternalTextureDownloadState();
            _externalTextureDownloadStates[key] = state;
            return state;
        }
    }

    private static void StartExternalTextureDownload(ExternalTextureCatalogItem item, bool force)
    {
        ExternalTextureDownloadState state = GetOrCreateExternalTextureDownloadState(item.StableKey);
        if (state.InFlight)
            return;

        state.InFlight = true;
        state.Status = "Queued";
        state.Error = null;

        _ = Task.Run(async () =>
        {
            try
            {
                string destinationRoot = GetExternalTextureDownloadRoot();
                string sourceFolder = item.Source == ExternalTextureSourceKind.PixelFurnace ? "PixelFurnace" : "FreePBR";
                string destinationFolder = Path.Combine(destinationRoot, sourceFolder, SanitizeFileName(item.Name));

                if (Directory.Exists(destinationFolder) && !force)
                {
                    state.Status = "Already downloaded";
                    state.ExtractedPath = destinationFolder;
                    return;
                }

                Directory.CreateDirectory(destinationRoot);
                string archivePath = Path.Combine(GetExternalTextureArchiveCacheDirectory(), item.Source.ToString(), $"{SanitizeFileName(item.Name)}.zip");
                Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

                state.Status = "Resolving";
                if (item.Source == ExternalTextureSourceKind.PixelFurnace)
                    await DownloadPixelFurnacePackAsync(item, archivePath, state).ConfigureAwait(false);
                else
                    await DownloadFreePbrPackAsync(item, archivePath, state).ConfigureAwait(false);

                if (!IsZipArchive(archivePath))
                    throw new InvalidDataException("Downloaded file is not a ZIP archive.");

                state.Status = "Extracting";
                if (Directory.Exists(destinationFolder))
                    Directory.Delete(destinationFolder, recursive: true);
                Directory.CreateDirectory(destinationFolder);
                ZipFile.ExtractToDirectory(archivePath, destinationFolder);

                state.ExtractedPath = destinationFolder;
                state.Status = "Downloaded";

                Engine.InvokeOnMainThread(() =>
                {
                    try
                    {
                        InvalidateAssetExplorerSnapshots(_assetExplorerGameState);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, "Failed to refresh Asset Explorer after external texture download.");
                    }
                }, "External Textures: Refresh asset explorer");
            }
            catch (Exception ex)
            {
                state.Error = ex.Message;
                state.Status = "Failed";
                Debug.LogException(ex, $"External texture download failed for '{item.Name}'.");
            }
            finally
            {
                state.InFlight = false;
            }
        });
    }

    private static async Task DownloadPixelFurnacePackAsync(ExternalTextureCatalogItem item, string archivePath, ExternalTextureDownloadState state)
    {
        string url = item.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Pixel-Furnace item has no download URL.");

        state.Status = "Downloading";
        using HttpResponseMessage response = await _externalTextureHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await WriteHttpContentToFileAsync(response.Content, archivePath).ConfigureAwait(false);
    }

    private static async Task DownloadFreePbrPackAsync(ExternalTextureCatalogItem item, string archivePath, ExternalTextureDownloadState state)
    {
        FreePbrDownloadDescriptor descriptor = await ResolveFreePbrBlDownloadAsync(item).ConfigureAwait(false);
        state.Status = "Downloading";
        using var content = new FormUrlEncodedContent(descriptor.Fields);
        using HttpResponseMessage response = await _externalTextureHttpClient.PostAsync(descriptor.PostUrl, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await WriteHttpContentToFileAsync(response.Content, archivePath).ConfigureAwait(false);
    }

    private static async Task<FreePbrDownloadDescriptor> ResolveFreePbrBlDownloadAsync(ExternalTextureCatalogItem item)
    {
        string html = await _externalTextureHttpClient.GetStringAsync(item.DetailUrl).ConfigureAwait(false);
        foreach (Match formMatch in Regex.Matches(html, "(?is)<form\\b(?<attrs>[^>]*)>(?<content>.*?)</form>", ExternalTextureRegexOptions))
        {
            string formHtml = formMatch.Groups["content"].Value;
            if (!formHtml.Contains("somdn_download_multi_single", StringComparison.OrdinalIgnoreCase))
                continue;

            string fileName = FindFreePbrBlFileName(formHtml);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            var fields = GetHiddenInputFields(formHtml);
            fields.TryAdd("action", "somdn_download_multi_single");
            fields.TryAdd("somdn_product", item.Id);

            foreach (string required in new[] { "action", "somdn_product", "somdn_productfile", "somdn_download_key" })
            {
                if (!fields.TryGetValue(required, out string? value) || string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException($"FreePBR download form is missing '{required}'.");
            }

            string action = GetHtmlAttribute(formMatch.Groups["attrs"].Value, "action");
            if (string.IsNullOrWhiteSpace(action))
                action = item.DetailUrl;

            return new FreePbrDownloadDescriptor
            {
                PostUrl = ResolveUrl("https://freepbr.com/", action),
                FileName = fileName,
                Fields = fields,
            };
        }

        throw new InvalidOperationException("No FreePBR BL ZIP download form was found.");
    }

    private static string FindFreePbrBlFileName(string formHtml)
    {
        foreach (Match linkMatch in Regex.Matches(formHtml, "(?is)<a\\b[^>]*>(?<text>.*?)</a>", ExternalTextureRegexOptions))
        {
            string candidate = PlainText(linkMatch.Groups["text"].Value);
            if (IsFreePbrBlArchive(candidate))
                return candidate;
        }

        string plain = PlainText(formHtml);
        foreach (Match zipMatch in Regex.Matches(plain, "(?i)[a-z0-9][a-z0-9._()+-]*\\.zip", ExternalTextureRegexOptions))
        {
            string candidate = zipMatch.Value;
            if (IsFreePbrBlArchive(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private static bool IsFreePbrBlArchive(string fileName)
        => Regex.IsMatch(fileName, "(?i)(^|[-_])bl(?:[-_][a-z0-9]+)?\\.zip$");

    private static Dictionary<string, string> GetHiddenInputFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match inputMatch in Regex.Matches(html, "(?is)<input\\b(?<attrs>[^>]*?)>", ExternalTextureRegexOptions))
        {
            string attrs = inputMatch.Groups["attrs"].Value;
            string name = GetHtmlAttribute(attrs, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            fields[name] = GetHtmlAttribute(attrs, "value") ?? string.Empty;
        }

        return fields;
    }

    private static async Task WriteHttpContentToFileAsync(HttpContent content, string archivePath)
    {
        string tempPath = archivePath + ".download";
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        await using (Stream input = await content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (Stream output = File.Create(tempPath))
            await input.CopyToAsync(output).ConfigureAwait(false);

        var info = new FileInfo(tempPath);
        if (info.Length <= 0)
            throw new InvalidDataException("Downloaded file is empty.");

        File.Move(tempPath, archivePath, overwrite: true);
    }

    private static bool IsExternalTextureDownloaded(ExternalTextureCatalogItem item, out string? folderPath)
    {
        string sourceFolder = item.Source == ExternalTextureSourceKind.PixelFurnace ? "PixelFurnace" : "FreePBR";
        folderPath = Path.Combine(GetExternalTextureDownloadRoot(), sourceFolder, SanitizeFileName(item.Name));
        return Directory.Exists(folderPath);
    }

    private static string GetExternalTextureDownloadRoot()
    {
        string gameAssetsPath = Engine.Assets?.GameAssetsPath ?? Path.Combine(AppContext.BaseDirectory, "Assets");
        string relative = NormalizeExternalTextureRelativeOutput(_externalTextureOutputRelativePath);
        return Path.GetFullPath(Path.Combine(gameAssetsPath, relative));
    }

    private static string NormalizeExternalTextureRelativeOutput(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ExternalTextureBrowserDefaultRelativeOutput;

        string normalized = value.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string[] parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] safeParts = parts
            .Where(part => part != "." && part != "..")
            .ToArray();
        return safeParts.Length == 0
            ? ExternalTextureBrowserDefaultRelativeOutput
            : Path.Combine(safeParts);
    }

    private static void OpenExternalTextureDownloadFolder()
    {
        try
        {
            string path = GetExternalTextureDownloadRoot();
            Directory.CreateDirectory(path);
            OpenPathInExplorer(path, isDirectory: true);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "Failed to open external texture download folder.");
        }
    }

    private static string GetExternalTexturePreviewCacheDirectory()
        => Path.Combine(GetExternalTextureCacheRoot(), "Previews");

    private static string GetExternalTextureArchiveCacheDirectory()
        => Path.Combine(GetExternalTextureCacheRoot(), "Archives");

    private static string GetExternalTextureCacheRoot()
    {
        string root = Engine.Assets?.GameAssetsPath ?? Path.Combine(AppContext.BaseDirectory, "Assets");
        string? projectRoot = Path.GetDirectoryName(Path.GetFullPath(root));
        if (string.IsNullOrWhiteSpace(projectRoot))
            projectRoot = AppContext.BaseDirectory;
        return Path.Combine(projectRoot, "Cache", "ExternalTextures");
    }

    private static bool IsZipArchive(string path)
    {
        if (!File.Exists(path))
            return false;

        using FileStream stream = File.OpenRead(path);
        if (stream.Length < 4)
            return false;

        Span<byte> signature = stackalloc byte[4];
        int read = stream.Read(signature);
        return read == 4 && signature[0] == 0x50 && signature[1] == 0x4B;
    }

    private static string GetPreviewCacheExtension(string previewUrl)
    {
        try
        {
            string ext = Path.GetExtension(new Uri(previewUrl).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 8)
                return ext;
        }
        catch
        {
        }

        return ".img";
    }

    private static string ComputeStableFileName(string text)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string SanitizeFileName(string name)
    {
        string sanitized = name;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(invalid, '_');
        sanitized = Regex.Replace(sanitized, "\\s+", " ").Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "ExternalTexture" : sanitized;
    }

    private static string PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        string text = Regex.Replace(html, "(?is)<br\\s*/?>", " ");
        text = Regex.Replace(text, "(?is)<[^>]+>", " ");
        text = HtmlDecode(text);
        text = Regex.Replace(text, "\\s+", " ");
        return text.Trim();
    }

    private static string HtmlDecode(string? text)
        => WebUtility.HtmlDecode(text ?? string.Empty);

    private static string GetHtmlAttribute(string attributes, string name)
    {
        Match match = Regex.Match(attributes, "(?is)\\b" + Regex.Escape(name) + "\\s*=\\s*[\"'](?<value>.*?)[\"']", ExternalTextureRegexOptions);
        return match.Success ? HtmlDecode(match.Groups["value"].Value) : string.Empty;
    }

    private static string ResolveUrl(string baseUrl, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return baseUrl;

        return new Uri(new Uri(baseUrl), url).AbsoluteUri;
    }

    private static string ExtractResolution(string text)
    {
        Match match = Regex.Match(text, "(?<w>\\d{3,5})\\s*[xX\\u00D7]\\s*(?<h>\\d{3,5})", ExternalTextureRegexOptions);
        return match.Success ? $"{match.Groups["w"].Value}x{match.Groups["h"].Value}" : string.Empty;
    }

    private static DateTime? ExtractWordPressDate(JToken product)
    {
        string? date = product.Value<string>("date_created_gmt")
            ?? product.Value<string>("date_modified_gmt")
            ?? product.Value<string>("date_created")
            ?? product.Value<string>("date_modified");
        return DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)
            ? parsed
            : null;
    }
}
