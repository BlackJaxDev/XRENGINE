using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Globalization;
using XREngine;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private static readonly List<OpenGLApiObjectRow> _openGlApiObjectScratch = new();
        private static GenericRenderObject? _selectedOpenGlRenderObject;
        private static AbstractRenderAPIObject? _selectedOpenGlApiObject;
        private static string _openGlApiSearch = string.Empty;
        private static string? _openGlWindowFilter;
        private static string? _openGlApiTypeFilter;
        private static string? _openGlXrTypeFilter;
        private static OpenGlApiGroupMode _openGlGroupMode = OpenGlApiGroupMode.ApiType;

        private readonly struct OpenGLApiObjectRow
        {
            public OpenGLApiObjectRow(
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
            ApiType,
            Window,
            RenderPipeline,
        }

        private static void DrawOpenGLApiObjectsPanel()
        {
            if (!_showOpenGLApiObjects) return;
            if (!ImGui.Begin("OpenGL API Objects", ref _showOpenGLApiObjects))
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
            if (!ImGui.Begin("OpenGL Errors", ref _showOpenGLErrors))
            {
                ImGui.End();
                return;
            }
            DrawOpenGLDebugTabContent();
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
                if (window?.Renderer is not OpenGLRenderer glRenderer)
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
                foreach (var viewport in window.Viewports)
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

                foreach (var pair in glRenderer.RenderObjectCache)
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
                int cmp = string.Compare(a.WindowTitle, b.WindowTitle, StringComparison.OrdinalIgnoreCase);
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
            string[] apiTypeOptions = rows.Count > 0
                ? rows.Select(static r => r.ApiType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static r => r, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            string[] xrTypeOptions = rows.Count > 0
                ? rows.Select(static r => r.XrType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static r => r, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();

            ImGui.TextUnformatted("Search:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220.0f);
            ImGui.InputTextWithHint("##OpenGlApiSearch", "Name, type, window, handle", ref _openGlApiSearch, 256);

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
                _openGlApiTypeFilter = null;
                _openGlXrTypeFilter = null;
            }

            if (windowOptions.Length > 0 || apiTypeOptions.Length > 0 || xrTypeOptions.Length > 0)
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
                        ? "No OpenGL API objects are currently generated."
                        : "No OpenGL API objects match the current filters.";
                    ImGui.TextDisabled(message);
                }
                else if (ImGui.BeginTable("ProfilerOpenGLApiObjectsTable", 4, tableFlags, new Vector2(-1.0f, -1.0f)))
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Window", ImGuiTableColumnFlags.WidthStretch, 0.25f);
                    ImGui.TableSetupColumn("API Object", ImGuiTableColumnFlags.WidthStretch, 0.3f);
                    ImGui.TableSetupColumn("XR Object", ImGuiTableColumnFlags.WidthStretch, 0.35f);
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
                        }

                        foreach (var row in group.Rows)
                        {
                            bool isSelected = ReferenceEquals(_selectedOpenGlRenderObject, row.RenderObject);
                            if (isSelected)
                                selectionVisible = true;

                            ImGui.TableNextRow();

                            ImGui.TableSetColumnIndex(0);
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

                            ImGui.TableSetColumnIndex(1);
                            ImGui.TextUnformatted(row.ApiName);
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(row.ApiType);

                            ImGui.TableSetColumnIndex(2);
                            ImGui.TextUnformatted(row.XrName);
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(row.XrType);

                            ImGui.TableSetColumnIndex(3);
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
            var errors = OpenGLRenderer.GetTrackedOpenGLErrors();
            if (errors.Count == 0)
            {
                ImGui.TextDisabled("No OpenGL debug errors are currently tracked.");
                if (ImGui.Button("Clear Tracked Errors"))
                    OpenGLRenderer.ClearTrackedOpenGLErrors();
                return;
            }

            int totalHits = 0;
            foreach (var info in errors)
                totalHits += info.Count;

            ImGui.TextUnformatted($"IDs: {errors.Count} | Hits: {totalHits}");

            if (ImGui.Button("Clear Tracked Errors"))
            {
                OpenGLRenderer.ClearTrackedOpenGLErrors();
                return;
            }

            ImGui.SameLine();
            ImGui.TextDisabled("Sorted by most recent");

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
            float estimatedHeight = MathF.Min(44.0f + errors.Count * ImGui.GetTextLineHeightWithSpacing(), 320.0f);

            if (ImGui.BeginTable("ProfilerOpenGLErrorTable", 7, tableFlags, new Vector2(-1.0f, estimatedHeight)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
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
                    ImGui.TextUnformatted(error.Id.ToString(CultureInfo.InvariantCulture));

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(error.Count.ToString(CultureInfo.InvariantCulture));

                    ImGui.TableNextColumn();
                    bool highlightSeverity = string.Equals(error.Severity, "High", StringComparison.OrdinalIgnoreCase);
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
}
