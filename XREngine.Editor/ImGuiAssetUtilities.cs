using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using XREngine;
using XREngine.Core.Files;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.OpenGL;

namespace XREngine.Editor;

internal static class ImGuiAssetUtilities
{
    public const string AssetPayloadType = "XR_ASSET_PATH";
    private const string AssetPickerPopupId = "AssetPicker";
    private const uint AssetPickerPreviewSize = 256;
    private const float AssetPickerPreviewFallbackEdge = 96.0f;
    private static readonly Dictionary<AssetPickerKey, object> _assetPickerStates = new();
    private const string AssetCreateReplacePopupId = "AssetCreateReplace";

    [ThreadStatic]
    private static HashSet<XRAsset>? _inlineInspectorStack;

    private sealed class AssetReferenceEqualityComparer : IEqualityComparer<XRAsset>
    {
        public static readonly AssetReferenceEqualityComparer Instance = new();

        public bool Equals(XRAsset? x, XRAsset? y) => ReferenceEquals(x, y);

        public int GetHashCode(XRAsset obj) => RuntimeHelpers.GetHashCode(obj);
    }

    public static void SetPathPayload(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string normalized = Path.GetFullPath(path);
        byte[] utf8 = Encoding.UTF8.GetBytes(normalized + '\0');
        var handle = GCHandle.Alloc(utf8, GCHandleType.Pinned);
        try
        {
            ImGui.SetDragDropPayload(AssetPayloadType, handle.AddrOfPinnedObject(), (uint)utf8.Length);
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }

    public static string? GetPathFromPayload(ImGuiPayloadPtr payload)
    {
        if (payload.Data == IntPtr.Zero || payload.DataSize == 0)
            return null;

        int size = (int)payload.DataSize;
        if (size <= 0)
            return null;

        try
        {
            // The payload includes a null terminator appended during creation.
            int length = Math.Max(0, size - 1);
            string? value = Marshal.PtrToStringUTF8(payload.Data, length);
            return value is null ? null : Path.GetFullPath(value);
        }
        catch
        {
            return null;
        }
    }

    public static void DrawAssetField<TAsset>(
        string id,
        TAsset? current,
        Action<TAsset?> assign,
        AssetFieldOptions? options = null,
        bool allowClear = true,
        bool allowCreateOrReplace = true)
        where TAsset : XRAsset
    {
        options ??= AssetFieldOptions.ForType<TAsset>();
        var state = GetPickerState<TAsset>(options);

        ImGui.PushID(id);

        var style = ImGui.GetStyle();
        float availableWidth = ImGui.GetContentRegionAvail().X;

        bool showClear = allowClear && current is not null;
        float clearWidth = showClear
            ? ImGui.CalcTextSize("Clear").X + style.FramePadding.X * 2.0f
            : 0.0f;

        bool canCreateOrReplace = allowCreateOrReplace
            && !typeof(TAsset).ContainsGenericParameters
            && EditorImGuiUI.HasCreatablePropertyTypes(typeof(TAsset));
        string createReplaceLabel = current is null ? "Create..." : "Replace...";
        float createReplaceWidth = canCreateOrReplace
            ? ImGui.CalcTextSize(createReplaceLabel).X + style.FramePadding.X * 2.0f
            : 0.0f;

        float browseWidth = ImGui.CalcTextSize("Browse").X + style.FramePadding.X * 2.0f;
        int buttonCount = 1 + (showClear ? 1 : 0) + (canCreateOrReplace ? 1 : 0);
        float fieldWidth = MathF.Max(80.0f, availableWidth - (clearWidth + createReplaceWidth + browseWidth) - style.ItemSpacing.X * buttonCount);

        bool openPopup = false;
        string preview = GetAssetDisplayName(current);
        if (ImGui.Selectable(preview, false, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(fieldWidth, 0.0f)))
            openPopup = true;

        if (current is not null)
        {
            string? path = current.FilePath ?? current.OriginalPath;
            if (!string.IsNullOrEmpty(path) && ImGui.IsItemHovered())
                ImGui.SetTooltip(path);
        }

        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload(AssetPayloadType);
            if (payload.Data != IntPtr.Zero && payload.DataSize > 0)
            {
                string? path = GetPathFromPayload(payload);
                if (!string.IsNullOrEmpty(path))
                {
                    var asset = LoadAssetFromPath<TAsset>(path);
                    if (asset is not null)
                        assign(asset);
                }
            }
            ImGui.EndDragDropTarget();
        }

        if (showClear)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
                assign(null);
        }

        if (canCreateOrReplace)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(createReplaceLabel))
                ImGui.OpenPopup(AssetCreateReplacePopupId);

            EditorImGuiUI.DrawCreatablePropertyTypePickerPopup(AssetCreateReplacePopupId, typeof(TAsset), selectedType =>
            {
                try
                {
                    if (Activator.CreateInstance(selectedType) is TAsset created)
                        assign(created);
                    else
                        Debug.LogWarning($"Unable to create asset instance of '{selectedType.FullName}'.");
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, $"Failed to create asset instance of '{selectedType.FullName}'.");
                }
            });
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Browse"))
            openPopup = true;

        if (openPopup)
            ImGui.OpenPopup(AssetPickerPopupId);

        if (ImGui.BeginPopup(AssetPickerPopupId))
        {
            DrawAssetPickerPopup(state, current, assign);
            ImGui.EndPopup();
        }

        if (current is not null)
            DrawInlineAssetInspector(current);

        ImGui.PopID();
    }

    private static void DrawAssetPickerPopup<TAsset>(AssetPickerState<TAsset> state, TAsset? current, Action<TAsset?> assign)
        where TAsset : XRAsset
    {
        if (state.NeedsRefresh)
            RefreshAssetPickerState(state, force: true);

        bool isTexturePicker = typeof(XRTexture2D).IsAssignableFrom(typeof(TAsset));

        bool includeGame = state.IncludeGame;
        if (ImGui.Checkbox("Game Assets", ref includeGame))
        {
            state.IncludeGame = includeGame;
            state.NeedsRefresh = true;
        }

        ImGui.SameLine();

        bool includeEngine = state.IncludeEngine;
        if (ImGui.Checkbox("Engine Assets", ref includeEngine))
        {
            state.IncludeEngine = includeEngine;
            state.NeedsRefresh = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh"))
            RefreshAssetPickerState(state, force: true);

        if (state.NeedsRefresh)
            RefreshAssetPickerState(state, force: true);

        ImGui.Separator();

        string searchBuffer = state.Search;
        if (ImGui.InputTextWithHint("##AssetSearch", "Search...", ref searchBuffer, 256u))
            state.Search = searchBuffer.Trim();

        ImGui.Separator();

        var filteredCandidates = EnumerateFilteredCandidates(state).ToList();
        int candidateCount = filteredCandidates.Count;

        Vector2 listSize = new(0.0f, 260.0f);
        AssetCandidate<TAsset>? hoveredCandidate = null;
        AssetCandidate<TAsset>? selectedCandidate = null;
        if (ImGui.BeginChild("AssetPickerList", listSize, ImGuiChildFlags.Border))
        {
            if (candidateCount == 0)
            {
                ImGui.TextDisabled("No assets found. Adjust filters or refresh.");
            }
            else
            {
                unsafe
                {
                    var clipper = new ImGuiListClipper();
                    ImGuiNative.ImGuiListClipper_Begin(&clipper, candidateCount, ImGui.GetTextLineHeightWithSpacing());
                    while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
                    {
                        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        {
                            var candidate = filteredCandidates[i];

                            if (isTexturePicker)
                                candidate.RequestPreview(AssetPickerPreviewSize);

                            bool selected = candidate.Matches(current);
                            string label = candidate.DisplayName;
                            if (candidate.IsEngine)
                                label += "  [Engine]";

                            if (ImGui.Selectable(label, selected))
                            {
                                candidate.RequestAsset(asset => assign(asset));
                                ImGui.CloseCurrentPopup();
                            }

                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip(candidate.Path);
                                hoveredCandidate = candidate;
                            }

                            if (selected)
                            {
                                ImGui.SetItemDefaultFocus();
                                selectedCandidate = candidate;
                            }
                        }
                    }
                    ImGuiNative.ImGuiListClipper_End(&clipper);
                }
            }

            ImGui.EndChild();
        }

        if (isTexturePicker)
        {
            AssetCandidate<TAsset>? previewCandidate = 
                hoveredCandidate
                ?? selectedCandidate
                ?? (current is not null ? filteredCandidates.FirstOrDefault(c => c.Matches(current)) : null)
                ?? state.LastPreviewCandidate;

            if (previewCandidate is null && filteredCandidates.Count > 0)
                previewCandidate = filteredCandidates[0];

            state.LastPreviewCandidate = previewCandidate;

            ImGui.Separator();
            DrawTexturePreviewPane(previewCandidate, MathF.Max(0.0f, ImGui.GetContentRegionAvail().X));
        }
        else
        {
            state.LastPreviewCandidate = null;
        }

        if (ImGui.Button("Clear Selection"))
        {
            assign(null);
            ImGui.CloseCurrentPopup();
        }
    }

    private static AssetPickerState<TAsset> GetPickerState<TAsset>(AssetFieldOptions options)
        where TAsset : XRAsset
    {
        string extensionKey = options.ExtensionsKey(typeof(TAsset));
        var key = new AssetPickerKey(typeof(TAsset), extensionKey);

        if (_assetPickerStates.TryGetValue(key, out var existing) && existing is AssetPickerState<TAsset> typed)
            return typed;

        var state = new AssetPickerState<TAsset>(options.ResolveExtensions(typeof(TAsset)));
        _assetPickerStates[key] = state;
        return state;
    }

    private static void RefreshAssetPickerState<TAsset>(AssetPickerState<TAsset> state, bool force = false)
        where TAsset : XRAsset
    {
        if (!force && !state.NeedsRefresh)
            return;

        state.NeedsRefresh = false;
        state.Candidates.Clear();
        state.LastPreviewCandidate = null;

        var assetManager = Engine.Assets;
        if (assetManager is null)
            return;

        var roots = new List<(string Path, bool IsEngine)>();
        if (state.IncludeGame && Directory.Exists(assetManager.GameAssetsPath))
            roots.Add((assetManager.GameAssetsPath, false));
        if (state.IncludeEngine && Directory.Exists(assetManager.EngineAssetsPath))
            roots.Add((assetManager.EngineAssetsPath, true));

        if (roots.Count == 0)
            return;

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, isEngine) in roots)
        {
            foreach (var file in EnumerateAssetFiles(root, state.Extensions))
            {
                if (!seenPaths.Add(file))
                    continue;

                TAsset? existing = null;
                if (assetManager.TryGetAssetByPath(file, out XRAsset? cached) && cached is TAsset typed)
                    existing = typed;

                string displayName = existing is not null && !string.IsNullOrWhiteSpace(existing.Name)
                    ? existing.Name
                    : Path.GetFileNameWithoutExtension(file);

                state.Candidates.Add(new AssetCandidate<TAsset>(file, isEngine, displayName, existing));
            }
        }

        state.Candidates.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<AssetCandidate<TAsset>> EnumerateFilteredCandidates<TAsset>(AssetPickerState<TAsset> state)
        where TAsset : XRAsset
    {
        string search = state.Search;
        if (string.IsNullOrWhiteSpace(search))
        {
            foreach (var candidate in state.Candidates)
                yield return candidate;
            yield break;
        }

        search = search.Trim();

        foreach (var candidate in state.Candidates)
        {
            if (candidate.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || candidate.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateAssetFiles(string root, IReadOnlyList<string> extensions)
    {
        if (!Directory.Exists(root))
            yield break;

        HashSet<string> extensionSet = new(extensions, StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (string file in files)
        {
            string ext = Path.GetExtension(file);
            if (extensionSet.Contains(ext))
                yield return Path.GetFullPath(file);
        }
    }

    private static TAsset? LoadAssetFromPath<TAsset>(string path) where TAsset : XRAsset
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var assets = Engine.Assets;
        if (assets is null)
            return null;

        try
        {
            return assets.Load(path, typeof(TAsset)) as TAsset;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load asset '{path}' as {typeof(TAsset).Name}: {ex.Message}");
            return null;
        }
    }

    private static string GetAssetDisplayName<T>(T? asset) where T : class
    {
        if (asset is null)
            return "<none>";

        string? preferredName = null;
        PropertyInfo? nameProperty = asset.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
        if (nameProperty?.GetValue(asset) is string name && !string.IsNullOrWhiteSpace(name))
            preferredName = name;

        return FormatAssetLabel(preferredName, asset);
    }

    private static string FormatAssetLabel(string? preferredName, object? fallback)
        => !string.IsNullOrWhiteSpace(preferredName) ? preferredName! : fallback?.GetType().Name ?? "<none>";

    private static readonly Dictionary<Type, string[]> DefaultExtensions = new()
    {
        { typeof(Model), new[] { ".asset" } },
        { typeof(XRMesh), new[] { ".asset", ".mesh", ".model" } },
        { typeof(XRMaterial), new[] { ".asset", ".material" } }
    };

    private static string[] GetDefaultExtensions(Type assetType)
        => DefaultExtensions.TryGetValue(assetType, out var exts)
            ? exts
            : [".asset"];

    public sealed class AssetFieldOptions
    {
        private readonly string[] _customExtensions;

        private AssetFieldOptions(IEnumerable<string>? extensions)
        {
            _customExtensions = extensions?
                .Select(NormalizeExtension)
                .Where(static e => !string.IsNullOrEmpty(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
        }

        public static AssetFieldOptions ForType<TAsset>() where TAsset : XRAsset
            => new(null);

        public static AssetFieldOptions ForMeshes()
            => new([".asset", ".mesh", ".model"]);

        public static AssetFieldOptions ForMaterials()
            => new([".asset", ".material"]);

        public static AssetFieldOptions WithExtensions(IEnumerable<string> extensions)
            => new(extensions);

        public string[] ResolveExtensions(Type assetType)
            => _customExtensions.Length > 0 ? _customExtensions : GetDefaultExtensions(assetType);

        public string ExtensionsKey(Type assetType)
            => _customExtensions.Length == 0 
                ? string.Join(';', GetDefaultExtensions(assetType))
                : string.Join(';', _customExtensions);

        private static string NormalizeExtension(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext))
                return string.Empty;

            string trimmed = ext.Trim();
            if (!trimmed.StartsWith('.'))
                trimmed = "." + trimmed;
            return trimmed.ToLowerInvariant();
        }
    }

    private sealed class AssetPickerState<TAsset>(string[] extensions)
        where TAsset : XRAsset
    {
        public string Search { get; set; } = string.Empty;
        public bool IncludeGame { get; set; } = true;
        public bool IncludeEngine { get; set; } = true;
        public bool NeedsRefresh { get; set; } = true;
        public IReadOnlyList<string> Extensions { get; } = extensions;
        public List<AssetCandidate<TAsset>> Candidates { get; } = [];
        public AssetCandidate<TAsset>? LastPreviewCandidate { get; set; }
    }

    private sealed class AssetCandidate<TAsset> where TAsset : XRAsset
    {
        private readonly List<Action<TAsset?>> _pendingAssignments = [];
        private TAsset? _asset;
        private XRTexture2D? _previewTexture;
        private bool _previewRequested;
        private bool _loadingAsset;

        public AssetCandidate(string path, bool isEngine, string displayName, TAsset? preloaded)
        {
            Path = path;
            IsEngine = isEngine;
            DisplayName = displayName;
            _asset = preloaded;
            UpdateDisplayNameFromAsset();
        }

        public string Path { get; }
        public bool IsEngine { get; }
        public string DisplayName { get; private set; }

        private static bool IsTextureCandidate => typeof(XRTexture2D).IsAssignableFrom(typeof(TAsset));

        public XRTexture2D? PreviewTexture => _previewTexture;

        public bool Matches(TAsset? asset)
        {
            if (asset is null)
                return false;

            if (_asset is not null && ReferenceEquals(_asset, asset))
                return true;

            string? assetPath = asset.FilePath ?? asset.OriginalPath;
            return !string.IsNullOrEmpty(assetPath)
                && string.Equals(Path, assetPath, StringComparison.OrdinalIgnoreCase);
        }

        public void RequestPreview(uint maxPreviewSize)
        {
            if (_previewRequested || !IsTextureCandidate)
                return;

            _previewRequested = true;

            XRTexture2D seedTexture = (_asset as XRTexture2D) ?? _previewTexture ?? new XRTexture2D();
            seedTexture.FilePath ??= Path;
            if (string.IsNullOrWhiteSpace(seedTexture.Name))
                seedTexture.Name = System.IO.Path.GetFileNameWithoutExtension(Path);

            XRTexture2D.SchedulePreviewJob(
                Path,
                seedTexture,
                maxPreviewSize,
                onFinished: tex => Engine.InvokeOnMainThread(() => _previewTexture = tex),
                onError: ex => Debug.LogException(ex, $"Texture preview job failed for '{Path}'."));
        }

        public void RequestAsset(Action<TAsset?> assign)
        {
            if (assign is null)
                return;

            if (_asset is not null)
            {
                assign(_asset);
                return;
            }

            if (IsTextureCandidate)
            {
                _pendingAssignments.Add(assign);
                if (_loadingAsset)
                    return;

                _loadingAsset = true;

                XRTexture2D placeholder = _previewTexture ?? new XRTexture2D();
                placeholder.FilePath ??= Path;
                if (string.IsNullOrWhiteSpace(placeholder.Name))
                    placeholder.Name = System.IO.Path.GetFileNameWithoutExtension(Path);

                XRTexture2D.ScheduleLoadJob(
                    Path,
                    placeholder,
                    onFinished: tex => Engine.InvokeOnMainThread(() =>
                    {
                        _asset = (TAsset)(object)tex;
                        UpdateDisplayNameFromAsset();
                        FlushAssignments(_asset);
                        _loadingAsset = false;
                    }),
                    onError: ex => Engine.InvokeOnMainThread(() =>
                    {
                        Debug.LogException(ex, $"Texture import job failed for '{Path}'.");
                        FlushAssignments(null);
                        _loadingAsset = false;
                    }),
                    onCanceled: () => Engine.InvokeOnMainThread(() =>
                    {
                        FlushAssignments(null);
                        _loadingAsset = false;
                    }));
            }
            else
            {
                var loaded = LoadAssetFromPath<TAsset>(Path);
                _asset = loaded;
                UpdateDisplayNameFromAsset();
                assign(loaded);
            }
        }

        private void UpdateDisplayNameFromAsset()
        {
            if (_asset is not null && !string.IsNullOrWhiteSpace(_asset.Name))
                DisplayName = _asset.Name;
        }

        private void FlushAssignments(TAsset? asset)
        {
            if (_pendingAssignments.Count == 0)
                return;

            foreach (var cb in _pendingAssignments)
                cb(asset);

            _pendingAssignments.Clear();
        }
    }

    private static void DrawTexturePreviewPane<TAsset>(AssetCandidate<TAsset>? candidate, float paneWidth)
        where TAsset : XRAsset
    {
        ImGui.TextDisabled("Preview");
        ImGui.Separator();

        if (candidate is null)
        {
            ImGui.TextDisabled("Select a texture to preview.");
            return;
        }

        candidate.RequestPreview(AssetPickerPreviewSize);
        XRTexture2D? texture = candidate.PreviewTexture;
        if (texture is null)
        {
            ImGui.TextDisabled("Loading preview...");
            return;
        }

        float usableWidth = paneWidth <= 0f ? AssetPickerPreviewSize : paneWidth;
        float maxEdge = MathF.Max(32f, MathF.Min(AssetPickerPreviewSize, usableWidth - ImGui.GetStyle().WindowPadding.X * 2f));
        if (TryGetTexturePreviewHandle(texture, maxEdge, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failureReason))
        {
            Vector2 cursor = ImGui.GetCursorPos();
            float offsetX = MathF.Max(0f, (usableWidth - displaySize.X) * 0.5f);
            ImGui.SetCursorPos(new Vector2(cursor.X + offsetX, cursor.Y));
            ImGui.Image(handle, displaySize);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ImGui.GetStyle().ItemSpacing.Y);
            ImGui.TextDisabled($"{(int)pixelSize.X} x {(int)pixelSize.Y}");
        }
        else
        {
            ImGui.TextDisabled(failureReason ?? "Preview unavailable");
        }

        ImGui.Separator();
        ImGui.TextWrapped(candidate.DisplayName);
        ImGui.TextDisabled(Path.GetFileName(candidate.Path));
    }

    private static bool TryGetTexturePreviewHandle(XRTexture2D texture, float maxEdge, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failureReason)
    {
        handle = nint.Zero;
        pixelSize = new Vector2(texture.Width, texture.Height);
        displaySize = new Vector2(AssetPickerPreviewFallbackEdge, AssetPickerPreviewFallbackEdge);
        failureReason = null;

        if (!Engine.IsRenderThread)
        {
            failureReason = "Preview unavailable off render thread";
            return false;
        }

        OpenGLRenderer? renderer = TryGetOpenGLRenderer();
        if (renderer is null)
        {
            failureReason = "Preview requires OpenGL renderer";
            return false;
        }

        var apiTexture = renderer.GenericToAPI<GLTexture2D>(texture);
        if (apiTexture is null)
        {
            failureReason = "Texture not uploaded";
            return false;
        }

        uint binding = apiTexture.BindingId;
        if (binding == OpenGLRenderer.GLObjectBase.InvalidBindingId || binding == 0)
        {
            failureReason = "Texture not ready";
            return false;
        }

        handle = (nint)binding;
        displaySize = GetPreviewSizeForEdge(pixelSize, maxEdge);
        return true;
    }

    private static Vector2 GetPreviewSizeForEdge(Vector2 pixelSize, float maxEdge)
    {
        float width = MathF.Max(pixelSize.X, 1f);
        float height = MathF.Max(pixelSize.Y, 1f);

        if (maxEdge <= 0f)
            return new Vector2(AssetPickerPreviewFallbackEdge, AssetPickerPreviewFallbackEdge);

        float largest = MathF.Max(width, height);
        if (largest <= maxEdge)
            return new Vector2(width, height);

        float scale = maxEdge / largest;
        return new Vector2(width * scale, height * scale);
    }

    private static OpenGLRenderer? TryGetOpenGLRenderer()
    {
        if (AbstractRenderer.Current is OpenGLRenderer current)
            return current;

        foreach (var window in Engine.Windows)
            if (window.Renderer is OpenGLRenderer renderer)
                return renderer;

        return null;
    }

    private static void DrawInlineAssetInspector(XRAsset asset)
    {
        _inlineInspectorStack ??= new HashSet<XRAsset>(AssetReferenceEqualityComparer.Instance);
        if (!_inlineInspectorStack.Add(asset))
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Inspect Asset: <circular reference>");
            return;
        }

        ImGui.Spacing();
        ImGui.PushID("InlineAssetInspector");
        const ImGuiTreeNodeFlags headerFlags = ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
        try
        {
            if (ImGui.CollapsingHeader("Inspect Asset", headerFlags))
            {
                DrawAssetInspectorMetadata(asset);
                ImGui.Separator();

                using var contextScope = RequiresExternalAssetContext(asset)
                    ? EditorImGuiUI.PushInspectorAssetContext(asset.SourceAsset ?? asset)
                    : null;

                EditorImGuiUI.DrawAssetInspectorInline(asset);
            }
            ImGui.PopID();
        }
        finally
        {
            _inlineInspectorStack.Remove(asset);
        }
    }

    private static void DrawAssetInspectorMetadata(XRAsset asset)
    {
        string displayName = !string.IsNullOrWhiteSpace(asset.Name)
            ? asset.Name!
            : asset.GetType().Name;
        ImGui.TextUnformatted(displayName);

        string? filePath = asset.FilePath;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            string fileName = Path.GetFileName(filePath);
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(fileName) ? filePath : fileName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(filePath);

            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Path"))
                ImGui.SetClipboardText(filePath);
        }
        else
        {
            ImGui.TextDisabled("Embedded asset (not saved as a separate file)");
        }

        if (asset.ID != Guid.Empty)
            ImGui.TextDisabled($"GUID: {asset.ID}");

        bool isDirty = asset.IsDirty;
        ImGui.TextDisabled(isDirty ? "Status: Dirty" : "Status: Saved");

        //if (!string.IsNullOrWhiteSpace(filePath))
        //{
        //    int referenceCount = AssetReferenceAnalyzer.GetReferenceCount(asset);
        //    string message = referenceCount switch
        //    {
        //        0 => "No other assets reference this file.",
        //        1 => "1 other asset references this file.",
        //        _ => $"{referenceCount} other assets reference this file."
        //    };
        //    ImGui.TextDisabled(message);
        //}
    }

    private static bool RequiresExternalAssetContext(XRAsset asset)
        => !string.IsNullOrWhiteSpace(asset.FilePath);

    private static class AssetReferenceAnalyzer
    {
        private static readonly Dictionary<Guid, CacheEntry> _cache = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);

        public static int GetReferenceCount(XRAsset asset)
        {
            if (asset.ID == Guid.Empty)
                return 0;

            DateTime now = DateTime.UtcNow;
            lock (_cache)
            {
                if (_cache.TryGetValue(asset.ID, out var entry)
                    && entry.Expiry > now
                    && entry.Asset.TryGetTarget(out var cached)
                    && ReferenceEquals(cached, asset))
                {
                    return entry.Count;
                }
            }

            int count = AssetReferenceWalker.CountReferences(asset);

            lock (_cache)
            {
                _cache[asset.ID] = new CacheEntry(new WeakReference<XRAsset>(asset), now + CacheDuration, count);
            }

            return count;
        }

        private sealed class CacheEntry
        {
            public CacheEntry(WeakReference<XRAsset> asset, DateTime expiry, int count)
            {
                Asset = asset;
                Expiry = expiry;
                Count = count;
            }

            public WeakReference<XRAsset> Asset { get; }
            public DateTime Expiry { get; }
            public int Count { get; }
        }
    }

    private sealed class AssetReferenceWalker
    {
        private const int MaxTraversalDepth = 64;
        private const int MaxEnumeratedElements = 2048;
        private static readonly ConcurrentDictionary<Type, List<Func<object, object?>>> AccessorCache = new();

        private readonly XRAsset _target;
        private readonly Guid _targetId;
        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);

        private AssetReferenceWalker(XRAsset target)
        {
            _target = target;
            _targetId = target.ID;
        }

        public static int CountReferences(XRAsset target)
        {
            var assets = Engine.Assets;
            if (assets is null)
                return 0;

            var walker = new AssetReferenceWalker(target);
            int count = 0;
            foreach (var candidate in assets.LoadedAssetsByIDInternal.Values)
            {
                if (candidate is null || ReferenceEquals(candidate, target))
                    continue;

                if (walker.ContainsReference(candidate))
                    count++;
            }

            return count;
        }

        private bool ContainsReference(object root)
        {
            _visited.Clear();
            return Traverse(root, 0);
        }

        private bool Traverse(object? candidate, int depth)
        {
            if (candidate is null)
                return false;

            if (ReferenceEquals(candidate, _target))
                return true;

            if (!_visited.Add(candidate))
                return false;

            if (candidate is string || candidate is Type)
                return false;

            Type type = candidate.GetType();

            if (ShouldSkipTraversalType(type))
                return false;
            if (type.IsPrimitive || type.IsEnum || type.IsPointer)
                return false;

            if (candidate is XRAsset asset && asset.ID != Guid.Empty && asset.ID == _targetId)
                return true;

            if (depth >= MaxTraversalDepth)
                return false;

            if (candidate is IDictionary dictionary)
            {
                int processed = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (Traverse(entry.Key, depth + 1) || Traverse(entry.Value, depth + 1))
                        return true;

                    if (++processed >= MaxEnumeratedElements)
                        break;
                }
                return false;
            }

            if (candidate is IEnumerable enumerable)
            {
                int processed = 0;
                foreach (var item in enumerable)
                {
                    if (Traverse(item, depth + 1))
                        return true;

                    if (++processed >= MaxEnumeratedElements)
                        break;
                }
                return false;
            }

            if (type.IsValueType)
                return false;

            foreach (var accessor in GetAccessors(type))
            {
                object? value;
                try
                {
                    value = accessor(candidate);
                }
                catch
                {
                    continue;
                }

                if (Traverse(value, depth + 1))
                    return true;
            }

            return false;
        }

        private static List<Func<object, object?>> GetAccessors(Type type)
            => AccessorCache.GetOrAdd(type, static t =>
            {
                var accessors = new List<Func<object, object?>>();
                for (Type? current = t; current is not null && current != typeof(object); current = current.BaseType)
                {
                    foreach (var property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!property.CanRead)
                            continue;
                        if (property.GetIndexParameters().Length != 0)
                            continue;
                        var getter = property.GetMethod;
                        if (getter is null || getter.IsStatic)
                            continue;
                        accessors.Add(property.GetValue);
                    }

                    foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (field.IsStatic)
                            continue;
                        accessors.Add(field.GetValue);
                    }
                }

                return accessors;
            });

        private static bool ShouldSkipTraversalType(Type type)
        {
            // System.Threading.Lock (.NET 9+) has internal state that throws on reflection access.
            if (type == typeof(System.Threading.Lock))
                return true;

            string? fullName = type.FullName;
            if (string.IsNullOrEmpty(fullName))
                return false;

            // CLR internal runtime caches have fragile reflection accessors that can crash traversal, so skip them entirely.
            return fullName.Contains("System.RuntimeType", StringComparison.Ordinal)
                && fullName.Contains("RuntimeTypeCache", StringComparison.Ordinal);
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(object obj)
            => RuntimeHelpers.GetHashCode(obj);
    }

    private readonly record struct AssetPickerKey(Type AssetType, string ExtensionsKey);
}
