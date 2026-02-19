using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Globalization;
using XREngine;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
        private static readonly List<OpenGLApiObjectRow> _openGlApiObjectScratch = new();
        private static GenericRenderObject? _selectedOpenGlRenderObject;
        private static AbstractRenderAPIObject? _selectedOpenGlApiObject;
        private static string _openGlApiSearch = string.Empty;
        private static string? _openGlWindowFilter;
        private static string? _openGlApiBackendFilter;
        private static string? _openGlApiTypeFilter;
        private static string? _openGlXrTypeFilter;
        private static OpenGlApiGroupMode _openGlGroupMode = OpenGlApiGroupMode.ApiType;

        private static string[]? _openGlExtensionsSource;
        private static string[] _openGlExtensionsSorted = Array.Empty<string>();

        private readonly struct OpenGLApiObjectRow
        {
            public OpenGLApiObjectRow(
                string apiBackend,
                string windowTitle,
                string apiType,
                string apiName,
                string xrType,
                string xrName,
                nint handle,
                GenericRenderObject renderObject,
                AbstractRenderAPIObject apiObject,
                string? pipelineName)
            {
                ApiBackend = apiBackend;
                WindowTitle = windowTitle;
                ApiType = apiType;
                ApiName = apiName;
                XrType = xrType;
                XrName = xrName;
                Handle = handle;
                RenderObject = renderObject;
                ApiObject = apiObject;
                PipelineName = pipelineName;
            }

            public string ApiBackend { get; }
            public string WindowTitle { get; }
            public string ApiType { get; }
            public string ApiName { get; }
            public string XrType { get; }
            public string XrName { get; }
            public nint Handle { get; }
            public GenericRenderObject RenderObject { get; }
            public AbstractRenderAPIObject ApiObject { get; }
            public string? PipelineName { get; }
        }

        private enum OpenGlApiGroupMode
        {
            None,
            Api,
            ApiType,
            Window,
            RenderPipeline,
        }

        private readonly struct RenderApiErrorRow
        {
            public RenderApiErrorRow(string apiBackend, int? id, int count, string severity, string type, string source, DateTime lastSeenUtc, string message)
            {
                ApiBackend = apiBackend;
                Id = id;
                Count = count;
                Severity = severity;
                Type = type;
                Source = source;
                LastSeenUtc = lastSeenUtc;
                Message = message;
            }

            public string ApiBackend { get; }
            public int? Id { get; }
            public int Count { get; }
            public string Severity { get; }
            public string Type { get; }
            public string Source { get; }
            public DateTime LastSeenUtc { get; }
            public string Message { get; }
        }

        private static void DrawOpenGLApiObjectsPanel()
        {
            if (!_showOpenGLApiObjects) return;
            if (!ImGui.Begin("Render API Objects", ref _showOpenGLApiObjects))
            {
                ImGui.End();
                return;
            }
            DrawOpenGLApiObjectsTabContent();
            ImGui.End();
        }

        private static void DrawOpenGLErrorsPanel()
        {
            if (!_showOpenGLErrors) return;
            if (!ImGui.Begin("Render API Errors", ref _showOpenGLErrors))
            {
                ImGui.End();
                return;
            }
            DrawOpenGLDebugTabContent();
            ImGui.End();
        }

        private static void DrawRenderApiExtensionsPanel()
        {
            if (!_showRenderApiExtensions) return;
            if (!ImGui.Begin("Render API Extensions", ref _showRenderApiExtensions))
            {
                ImGui.End();
                return;
            }

            DrawRenderApiExtensionsContent();
            ImGui.End();
        }

        private static void DrawOpenGLApiObjectsTabContent()
        {
            var rows = _openGlApiObjectScratch;
            rows.Clear();

            // Collect all pipeline instances for ownership lookup
            var pipelineOwnership = new Dictionary<GenericRenderObject, string>(ReferenceEqualityComparer.Instance);

            foreach (var window in Engine.Windows)
            {
                if (window?.Renderer is not AbstractRenderer renderer)
                    continue;

                string apiBackend = renderer switch
                {
                    OpenGLRenderer => "OpenGL",
                    VulkanRenderer => "Vulkan",
                    _ => string.Empty,
                };

                if (string.IsNullOrEmpty(apiBackend))
                    continue;

                string windowTitle;
                try
                {
                    windowTitle = window.Window?.Title ?? string.Empty;
                }
                catch
                {
                    windowTitle = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(windowTitle))
                    windowTitle = $"Window 0x{window.GetHashCode():X}";

                // Iterate viewports to find pipeline ownership
                foreach (var viewport in Engine.EnumerateActiveViewports(window))
                {
                    var pipelineInstance = viewport?.RenderPipelineInstance;
                    var pipeline = pipelineInstance?.Pipeline;
                    if (pipelineInstance is null || pipeline is null)
                        continue;

                    string pipelineName = pipeline.DebugName;

                    // Check FBOs owned by this pipeline
                    foreach (var fbo in pipelineInstance.Resources.EnumerateFrameBufferInstances())
                    {
                        if (!pipelineOwnership.ContainsKey(fbo))
                            pipelineOwnership[fbo] = pipelineName;

                        // Also tag the textures within this FBO
                        if (fbo.Targets is not null)
                        {
                            foreach (var (target, _, _, _) in fbo.Targets)
                            {
                                if (target is GenericRenderObject targetRenderObject && !pipelineOwnership.ContainsKey(targetRenderObject))
                                    pipelineOwnership[targetRenderObject] = pipelineName;
                            }
                        }
                    }

                    // Check textures owned by this pipeline
                    foreach (var tex in pipelineInstance.Resources.EnumerateTextureInstances())
                    {
                        if (!pipelineOwnership.ContainsKey(tex))
                            pipelineOwnership[tex] = pipelineName;
                    }
                }

                foreach (var pair in renderer.RenderObjectCache)
                {
                    var renderObject = pair.Key;
                    var apiObject = pair.Value;

                    if (renderObject is null || apiObject is null)
                        continue;

                    if (!IsXrRenderObject(renderObject))
                        continue;

                    bool isGenerated;
                    try
                    {
                        isGenerated = apiObject.IsGenerated;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!isGenerated)
                        continue;

                    string apiName;
                    try
                    {
                        apiName = apiObject.GetDescribingName();
                    }
                    catch
                    {
                        apiName = apiObject.GetType().Name;
                    }

                    string xrName;
                    try
                    {
                        xrName = renderObject.GetDescribingName();
                    }
                    catch
                    {
                        xrName = renderObject.GetType().Name;
                    }

                    // Look up pipeline ownership
                    pipelineOwnership.TryGetValue(renderObject, out string? pipelineName);

                    rows.Add(new OpenGLApiObjectRow(
                        apiBackend,
                        windowTitle,
                        apiObject.GetType().Name,
                        apiName,
                        renderObject.GetType().Name,
                        xrName,
                        apiObject.GetHandle(),
                        renderObject,
                        apiObject,
                        pipelineName));
                }
            }

            rows.Sort(static (a, b) =>
            {
                int cmp = string.Compare(a.ApiBackend, b.ApiBackend, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0)
                    return cmp;

                cmp = string.Compare(a.WindowTitle, b.WindowTitle, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0)
                    return cmp;

                cmp = string.Compare(a.ApiType, b.ApiType, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0)
                    return cmp;

                return string.Compare(a.XrName, b.XrName, StringComparison.OrdinalIgnoreCase);
            });

            string[] windowOptions = rows.Count > 0
                ? rows.Select(static r => r.WindowTitle).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static r => r, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            string[] apiBackendOptions = rows.Count > 0
                ? rows.Select(static r => r.ApiBackend).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static r => r, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            string[] apiTypeOptions = rows.Count > 0
                ? rows.Select(static r => r.ApiType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static r => r, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            string[] xrTypeOptions = rows.Count > 0
                ? rows.Select(static r => r.XrType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static r => r, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();

            ImGui.TextUnformatted("Search:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220.0f);
            ImGui.InputTextWithHint("##OpenGlApiSearch", "Name, API, type, window, handle", ref _openGlApiSearch, 256);

            ImGui.SameLine();
            ImGui.TextUnformatted("Group:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140.0f);
            DrawOpenGlApiGroupCombo("##OpenGlApiGroupMode");

            ImGui.SameLine();
            if (ImGui.Button("Reset Filters"))
            {
                _openGlApiSearch = string.Empty;
                _openGlWindowFilter = null;
                _openGlApiBackendFilter = null;
                _openGlApiTypeFilter = null;
                _openGlXrTypeFilter = null;
            }

            if (windowOptions.Length > 0 || apiBackendOptions.Length > 0 || apiTypeOptions.Length > 0 || xrTypeOptions.Length > 0)
            {
                ImGui.Spacing();
                bool anyFilterDrawn = false;

                if (windowOptions.Length > 0)
                {
                    if (anyFilterDrawn)
                        ImGui.SameLine();
                    ImGui.SetNextItemWidth(180.0f);
                    DrawOpenGlFilterCombo("Window##OpenGlWindowFilter", windowOptions, ref _openGlWindowFilter);
                    anyFilterDrawn = true;
                }

                if (apiBackendOptions.Length > 0)
                {
                    if (anyFilterDrawn)
                        ImGui.SameLine();
                    ImGui.SetNextItemWidth(140.0f);
                    DrawOpenGlFilterCombo("API##OpenGlApiBackendFilter", apiBackendOptions, ref _openGlApiBackendFilter);
                    anyFilterDrawn = true;
                }

                if (apiTypeOptions.Length > 0)
                {
                    if (anyFilterDrawn)
                        ImGui.SameLine();
                    ImGui.SetNextItemWidth(180.0f);
                    DrawOpenGlFilterCombo("API Type##OpenGlApiTypeFilter", apiTypeOptions, ref _openGlApiTypeFilter);
                    anyFilterDrawn = true;
                }

                if (xrTypeOptions.Length > 0)
                {
                    if (anyFilterDrawn)
                        ImGui.SameLine();
                    ImGui.SetNextItemWidth(180.0f);
                    DrawOpenGlFilterCombo("XR Type##OpenGlXrTypeFilter", xrTypeOptions, ref _openGlXrTypeFilter);
                }
            }

            IEnumerable<OpenGLApiObjectRow> query = rows;

            if (!string.IsNullOrWhiteSpace(_openGlApiSearch))
            {
                string[] tokens = _openGlApiSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length > 0)
                    query = query.Where(row => OpenGlApiRowMatchesSearch(row, tokens));
            }

            if (!string.IsNullOrEmpty(_openGlWindowFilter))
            {
                string filter = _openGlWindowFilter!;
                query = query.Where(row => string.Equals(row.WindowTitle, filter, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(_openGlApiTypeFilter))
            {
                string filter = _openGlApiTypeFilter!;
                query = query.Where(row => string.Equals(row.ApiType, filter, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(_openGlApiBackendFilter))
            {
                string filter = _openGlApiBackendFilter!;
                query = query.Where(row => string.Equals(row.ApiBackend, filter, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(_openGlXrTypeFilter))
            {
                string filter = _openGlXrTypeFilter!;
                query = query.Where(row => string.Equals(row.XrType, filter, StringComparison.OrdinalIgnoreCase));
            }

            List<OpenGLApiObjectRow> filteredRows = query as List<OpenGLApiObjectRow> ?? query.ToList();

            if (filteredRows.Count == rows.Count)
                ImGui.TextUnformatted($"Tracked Objects: {rows.Count}");
            else
                ImGui.TextUnformatted($"Matching Objects: {filteredRows.Count} / {rows.Count}");

            Vector2 contentHeight = ImGui.GetContentRegionAvail();
            if (contentHeight.Y <= 0.0f)
                contentHeight.Y = 200.0f;

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
            bool selectionVisible = false;

            if (ImGui.BeginChild("OpenGLApiObjectsList", new Vector2(-1.0f, contentHeight.Y), ImGuiChildFlags.Border))
            {
                if (filteredRows.Count == 0)
                {
                    string message = rows.Count == 0
                        ? "No Render API objects are currently generated."
                        : "No Render API objects match the current filters.";
                    ImGui.TextDisabled(message);
                }
                else if (ImGui.BeginTable("ProfilerRenderApiObjectsTable", 5, tableFlags, new Vector2(-1.0f, -1.0f)))
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("API", ImGuiTableColumnFlags.WidthFixed, 90.0f);
                    ImGui.TableSetupColumn("Window", ImGuiTableColumnFlags.WidthStretch, 0.2f);
                    ImGui.TableSetupColumn("API Object", ImGuiTableColumnFlags.WidthStretch, 0.27f);
                    ImGui.TableSetupColumn("XR Object", ImGuiTableColumnFlags.WidthStretch, 0.33f);
                    ImGui.TableSetupColumn("Handle", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                    ImGui.TableHeadersRow();

                    int rowIndex = 0;

                    foreach (var group in EnumerateOpenGlGroups(filteredRows))
                    {
                        if (group.Header is not null)
                        {
                            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                            ImGui.TableSetColumnIndex(0);
                            ImGui.TextUnformatted($"{group.Header} ({group.Rows.Count})");
                            ImGui.TableSetColumnIndex(1);
                            ImGui.TableSetColumnIndex(2);
                            ImGui.TableSetColumnIndex(3);
                            ImGui.TableSetColumnIndex(4);
                        }

                        foreach (var row in group.Rows)
                        {
                            bool isSelected = ReferenceEquals(_selectedOpenGlRenderObject, row.RenderObject);
                            if (isSelected)
                                selectionVisible = true;

                            ImGui.TableNextRow();

                            ImGui.TableSetColumnIndex(0);
                            ImGui.TextUnformatted(row.ApiBackend);

                            ImGui.TableSetColumnIndex(1);
                            ImGui.PushID(rowIndex);
                            string label = $"{row.WindowTitle}##OpenGLApiRow";
                            if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                            {
                                var capturedRow = row;
                                SetInspectorStandaloneTarget(capturedRow.RenderObject, $"{capturedRow.XrName} ({capturedRow.XrType})", () =>
                                {
                                    if (ReferenceEquals(_selectedOpenGlRenderObject, capturedRow.RenderObject))
                                    {
                                        _selectedOpenGlRenderObject = null;
                                        _selectedOpenGlApiObject = null;
                                    }
                                });
                                _selectedOpenGlRenderObject = capturedRow.RenderObject;
                                _selectedOpenGlApiObject = capturedRow.ApiObject;
                                selectionVisible = true;
                                isSelected = true;
                            }
                            ImGui.PopID();

                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(row.WindowTitle);

                            ImGui.TableSetColumnIndex(2);
                            ImGui.TextUnformatted(row.ApiName);
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(row.ApiType);

                            ImGui.TableSetColumnIndex(3);
                            ImGui.TextUnformatted(row.XrName);
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(row.XrType);

                            ImGui.TableSetColumnIndex(4);
                            ulong handleValue = unchecked((ulong)row.Handle);
                            string handleLabel = handleValue == 0 ? "0x0" : $"0x{handleValue:X}";
                            ImGui.TextUnformatted(handleLabel);

                            rowIndex++;
                        }
                    }

                    ImGui.EndTable();
                }
                ImGui.EndChild();
            }

            if (!selectionVisible && _selectedOpenGlRenderObject is not null)
            {
                if (ReferenceEquals(_inspectorStandaloneTarget, _selectedOpenGlRenderObject))
                    ClearInspectorStandaloneTarget();
                _selectedOpenGlRenderObject = null;
                _selectedOpenGlApiObject = null;
            }

            rows.Clear();
        }

        private static void DrawOpenGLDebugTabContent()
        {
            List<RenderApiErrorRow> errors = CollectRenderApiErrors();
            if (errors.Count == 0)
            {
                ImGui.TextDisabled("No OpenGL or Vulkan errors are currently tracked.");
                if (ImGui.Button("Clear Tracked Errors"))
                {
                    OpenGLRenderer.ClearTrackedOpenGLErrors();
                    Debug.ClearConsoleEntries(ELogCategory.OpenGL);
                    Debug.ClearConsoleEntries(ELogCategory.Vulkan);
                }
                return;
            }

            int totalHits = 0;
            int uniqueIds = 0;
            foreach (var error in errors)
            {
                totalHits += Math.Max(error.Count, 1);
                if (error.Id.HasValue)
                    uniqueIds++;
            }

            ImGui.TextUnformatted($"Entries: {errors.Count} | IDs: {uniqueIds} | Hits: {totalHits}");

            if (ImGui.Button("Clear Tracked Errors"))
            {
                OpenGLRenderer.ClearTrackedOpenGLErrors();
                Debug.ClearConsoleEntries(ELogCategory.OpenGL);
                Debug.ClearConsoleEntries(ELogCategory.Vulkan);
                return;
            }

            ImGui.SameLine();
            ImGui.TextDisabled("Sorted by most recent");

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
            float estimatedHeight = MathF.Min(44.0f + errors.Count * ImGui.GetTextLineHeightWithSpacing(), 320.0f);

            if (ImGui.BeginTable("ProfilerRenderApiErrorTable", 8, tableFlags, new Vector2(-1.0f, estimatedHeight)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("API", ImGuiTableColumnFlags.WidthFixed, 80.0f);
                ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 70.0f);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("Severity", ImGuiTableColumnFlags.WidthFixed, 90.0f);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                ImGui.TableSetupColumn("Last Seen", ImGuiTableColumnFlags.WidthFixed, 140.0f);
                ImGui.TableSetupColumn("Latest Message", ImGuiTableColumnFlags.None);
                ImGui.TableHeadersRow();

                foreach (var error in errors.OrderByDescending(static e => e.LastSeenUtc))
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(error.ApiBackend);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(error.Id?.ToString(CultureInfo.InvariantCulture) ?? "-");

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(error.Count.ToString(CultureInfo.InvariantCulture));

                    ImGui.TableNextColumn();
                    bool highlightSeverity =
                        string.Equals(error.Severity, "High", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(error.Severity, "Error", StringComparison.OrdinalIgnoreCase);
                    if (highlightSeverity)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.45f, 0.45f, 1.0f));
                    ImGui.TextUnformatted(error.Severity);
                    if (highlightSeverity)
                        ImGui.PopStyleColor();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(error.Type);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(error.Source);

                    ImGui.TableNextColumn();
                    string lastSeenLocal = error.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    ImGui.TextUnformatted(lastSeenLocal);

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(error.Message);
                }

                ImGui.EndTable();
            }
        }

        private static List<RenderApiErrorRow> CollectRenderApiErrors()
        {
            var rows = new List<RenderApiErrorRow>();

            foreach (var error in OpenGLRenderer.GetTrackedOpenGLErrors())
            {
                rows.Add(new RenderApiErrorRow(
                    "OpenGL",
                    error.Id,
                    Math.Max(error.Count, 1),
                    error.Severity,
                    error.Type,
                    error.Source,
                    error.LastSeenUtc,
                    error.Message));
            }

            foreach (var entry in Debug.GetConsoleEntries())
            {
                if (entry.Category is not (ELogCategory.OpenGL or ELogCategory.Vulkan))
                    continue;

                if (!TryParseLogSeverity(entry.Message, out string severity))
                    continue;

                rows.Add(new RenderApiErrorRow(
                    entry.Category == ELogCategory.Vulkan ? "Vulkan" : "OpenGL",
                    null,
                    Math.Max(entry.RepeatCount, 1),
                    severity,
                    "Runtime Log",
                    "Debug",
                    entry.Timestamp.ToUniversalTime(),
                    ExtractPrimaryLogLine(entry.Message)));
            }

            return rows;
        }

        private static bool TryParseLogSeverity(string message, out string severity)
        {
            if (message.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
            {
                severity = "Error";
                return true;
            }

            if (message.Contains("[WARN]", StringComparison.OrdinalIgnoreCase))
            {
                severity = "Warning";
                return true;
            }

            severity = string.Empty;
            return false;
        }

        private static string ExtractPrimaryLogLine(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            int lineBreakIndex = message.IndexOfAny(['\r', '\n']);
            return lineBreakIndex >= 0 ? message[..lineBreakIndex] : message;
        }

        private static void DrawRenderApiExtensionsContent()
        {
            string[] openGlExtensions = GetSortedOpenGLExtensions();
            (string[] vulkanAvailable, string[] vulkanEnabled) = GetSortedVulkanExtensions();

            DrawRenderApiExtensionsSection("OpenGL", openGlExtensions, openGlExtensions);
            ImGui.Spacing();
            DrawRenderApiExtensionsSection("Vulkan", vulkanAvailable, vulkanEnabled);
        }

        private static void DrawRenderApiExtensionsSection(string apiName, IReadOnlyList<string> available, IReadOnlyCollection<string> enabled)
        {
            if (!ImGui.CollapsingHeader($"{apiName} Extensions", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            ImGui.TextUnformatted($"Available: {available.Count} | Activated: {enabled.Count}");

            Vector2 contentAvail = ImGui.GetContentRegionAvail();
            float listHeight = MathF.Max(180.0f, MathF.Min(360.0f, contentAvail.Y * 0.45f));

            if (!ImGui.BeginTable($"RenderApiExtensions_{apiName}", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1.0f, listHeight)))
                return;

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Extension", ImGuiTableColumnFlags.WidthStretch, 0.82f);
            ImGui.TableSetupColumn("Activated", ImGuiTableColumnFlags.WidthFixed, 90.0f);
            ImGui.TableHeadersRow();

            if (available.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled("No extensions were reported.");
                ImGui.TableNextColumn();
                ImGui.TextDisabled("-");
            }
            else
            {
                foreach (string extension in available)
                {
                    bool isEnabled = enabled.Contains(extension);

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(extension);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(isEnabled ? "Yes" : "No");
                }
            }

            ImGui.EndTable();
        }

        private static (string[] Available, string[] Enabled) GetSortedVulkanExtensions()
        {
            HashSet<string> available = new(StringComparer.Ordinal);
            HashSet<string> enabled = new(StringComparer.Ordinal);

            foreach (var window in Engine.Windows)
            {
                if (window?.Renderer is not VulkanRenderer vkRenderer)
                    continue;

                foreach (string extension in vkRenderer.AvailableDeviceExtensions)
                {
                    if (!string.IsNullOrWhiteSpace(extension))
                        available.Add(extension);
                }

                foreach (string extension in vkRenderer.EnabledDeviceExtensions)
                {
                    if (!string.IsNullOrWhiteSpace(extension))
                        enabled.Add(extension);
                }
            }

            string[] availableArray = [.. available.OrderBy(static name => name, StringComparer.Ordinal)];
            string[] enabledArray = [.. enabled.OrderBy(static name => name, StringComparer.Ordinal)];

            return (availableArray, enabledArray);
        }

        private static string[] GetSortedOpenGLExtensions()
        {
            // Cache the sorted list so we don't allocate/sort every frame.
            string[] source = Engine.Rendering.State.OpenGLExtensions;
            if (ReferenceEquals(_openGlExtensionsSource, source))
                return _openGlExtensionsSorted;

            _openGlExtensionsSource = source;
            if (source is null || source.Length == 0)
            {
                _openGlExtensionsSorted = Array.Empty<string>();
                return _openGlExtensionsSorted;
            }

            // Sort for scanability in the UI.
            var copy = source.Where(static s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static s => s, StringComparer.Ordinal)
                .ToArray();

            _openGlExtensionsSorted = copy;
            return _openGlExtensionsSorted;
        }

        private static IEnumerable<(string? Header, List<OpenGLApiObjectRow> Rows)> EnumerateOpenGlGroups(List<OpenGLApiObjectRow> rows)
        {
            if (_openGlGroupMode == OpenGlApiGroupMode.None)
            {
                yield return (null, rows);
                yield break;
            }

            var comparer = StringComparer.OrdinalIgnoreCase;
            var lookup = new Dictionary<string, List<OpenGLApiObjectRow>>(comparer);
            List<OpenGLApiObjectRow>? unownedList = null;

            foreach (var row in rows)
            {
                string key = _openGlGroupMode switch
                {
                    OpenGlApiGroupMode.Api => row.ApiBackend,
                    OpenGlApiGroupMode.ApiType => row.ApiType,
                    OpenGlApiGroupMode.RenderPipeline => row.PipelineName ?? string.Empty,
                    _ => row.WindowTitle
                };

                // For RenderPipeline mode, group unowned items separately
                if (_openGlGroupMode == OpenGlApiGroupMode.RenderPipeline && string.IsNullOrEmpty(key))
                {
                    unownedList ??= new List<OpenGLApiObjectRow>();
                    unownedList.Add(row);
                    continue;
                }

                if (!lookup.TryGetValue(key, out var list))
                {
                    list = new List<OpenGLApiObjectRow>();
                    lookup.Add(key, list);
                }

                list.Add(row);
            }

            foreach (var key in lookup.Keys.OrderBy(k => k, comparer))
                yield return (key, lookup[key]);

            // Yield unowned items last in RenderPipeline mode
            if (unownedList is not null && unownedList.Count > 0)
                yield return ("<Unowned>", unownedList);
        }

        private static void DrawOpenGlFilterCombo(string label, IReadOnlyList<string> options, ref string? current)
        {
            string preview = current ?? "All";
            if (!ImGui.BeginCombo(label, preview))
                return;

            bool isAllSelected = current is null;
            if (ImGui.Selectable("All", isAllSelected))
                current = null;
            if (isAllSelected)
                ImGui.SetItemDefaultFocus();

            for (int i = 0; i < options.Count; i++)
            {
                string option = options[i];
                bool selected = current is not null && string.Equals(option, current, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(option, selected))
                    current = option;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        private static void DrawOpenGlApiGroupCombo(string label)
        {
            string preview = GetGroupModeLabel(_openGlGroupMode);
            if (!ImGui.BeginCombo(label, preview))
                return;

            foreach (OpenGlApiGroupMode mode in Enum.GetValues<OpenGlApiGroupMode>())
            {
                string optionLabel = GetGroupModeLabel(mode);
                bool selected = mode == _openGlGroupMode;
                if (ImGui.Selectable(optionLabel, selected) && !selected)
                    _openGlGroupMode = mode;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        private static string GetGroupModeLabel(OpenGlApiGroupMode mode)
            => mode switch
            {
                OpenGlApiGroupMode.Api => "API",
                OpenGlApiGroupMode.ApiType => "API Type",
                OpenGlApiGroupMode.Window => "Window",
                OpenGlApiGroupMode.RenderPipeline => "Render Pipeline",
                _ => "None",
            };

        private static bool OpenGlApiRowMatchesSearch(OpenGLApiObjectRow row, IReadOnlyList<string> tokens)
        {
            if (tokens.Count == 0)
                return true;

            foreach (var token in tokens)
            {
                if (!OpenGlApiRowContainsToken(row, token))
                    return false;
            }

            return true;
        }

        private static bool OpenGlApiRowContainsToken(OpenGLApiObjectRow row, string token)
        {
            if (string.IsNullOrEmpty(token))
                return true;

            if (row.WindowTitle.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                row.ApiBackend.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                row.ApiName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                row.ApiType.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                row.XrName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                row.XrType.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                (row.PipelineName is not null && row.PipelineName.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            ulong handleValue = unchecked((ulong)row.Handle);
            string handleHex = handleValue == 0 ? "0x0" : $"0x{handleValue:X}";
            if (handleHex.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;

            string handleDecimal = handleValue.ToString(CultureInfo.InvariantCulture);
            return handleDecimal.Contains(token, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsXrRenderObject(GenericRenderObject renderObject)
        {
            Type? type = renderObject.GetType();
            while (type is not null)
            {
                if (type.Name.StartsWith("XR", StringComparison.OrdinalIgnoreCase))
                    return true;

            type = type.DeclaringType;
        }

        return false;
    }
}
