using ImGuiNET;
using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// ImGui-based window for the Shader Analyzer Tool.
/// Analyzes GLSL shaders and estimates GPU cycle costs.
/// Accessible from Tools menu.
/// </summary>
public class ShaderAnalyzerWindow
{
    private static ShaderAnalyzerWindow? _instance;
    public static ShaderAnalyzerWindow Instance => _instance ??= new ShaderAnalyzerWindow();

    private readonly GlslCostEstimator _estimator = new();
    private bool _isOpen = false;
    private string _shaderSourcePath = "";
    private string _shaderSource = "";
    private int _invocationsPerFrame = 1920 * 1080; // Default to 1080p resolution
    private ShaderCostReport? _report;
    private string _errorMessage = "";

    // UI State
    private bool _showByCategory = true;
    private string _categoryFilter = "";
    private bool _sortByOccurrences = false;
    private int _selectedCategoryIndex = -1;
    private Vector2 _sourceScrollPos = Vector2.Zero;

    // Preset resolutions for invocation estimation
    private static readonly (string Name, int Pixels)[] ResolutionPresets =
    [
        ("720p (1280x720)", 1280 * 720),
        ("1080p (1920x1080)", 1920 * 1080),
        ("1440p (2560x1440)", 2560 * 1440),
        ("4K (3840x2160)", 3840 * 2160),
        ("Custom", -1)
    ];
    private int _selectedResolutionPreset = 1; // Default to 1080p

    public bool IsOpen
    {
        get => _isOpen;
        set => _isOpen = value;
    }

    public void Open() => _isOpen = true;
    public void Close() => _isOpen = false;
    public void Toggle() => _isOpen = !_isOpen;

    /// <summary>
    /// Renders the ImGui window. Call this every frame from your ImGui render loop.
    /// </summary>
    public void Render()
    {
        if (!_isOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(1000, 750), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Shader Analyzer", ref _isOpen, ImGuiWindowFlags.MenuBar))
        {
            DrawMenuBar();
            DrawMainContent();
        }
        ImGui.End();
    }

    private void DrawMenuBar()
    {
        if (ImGui.BeginMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Load Shader..."))
                    ImGui.OpenPopup("LoadShaderPopup");

                if (ImGui.MenuItem("Paste from Clipboard"))
                    PasteFromClipboard();

                ImGui.Separator();

                if (ImGui.MenuItem("Export Report...", null, false, _report != null))
                    ImGui.OpenPopup("ExportReportPopup");

                ImGui.Separator();

                if (ImGui.MenuItem("Close"))
                    _isOpen = false;

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("View"))
            {
                if (ImGui.MenuItem("Group by Category", null, _showByCategory))
                    _showByCategory = !_showByCategory;

                if (ImGui.MenuItem("Sort by Occurrences", null, _sortByOccurrences))
                    _sortByOccurrences = !_sortByOccurrences;

                ImGui.EndMenu();
            }

            //if (ImGui.BeginMenu("Help"))
            //{
            //    if (ImGui.MenuItem("About"))
            //        ImGui.OpenPopup("AboutPopup");

            //    ImGui.EndMenu();
            //}

            ImGui.EndMenuBar();
        }

        //// About popup
        //if (ImGui.BeginPopup("AboutPopup"))
        //{
        //    ImGui.Text("Shader Analyzer Tool");
        //    ImGui.Separator();
        //    ImGui.TextWrapped(
        //        "Analyzes GLSL shaders and estimates GPU cycle costs.\n\n" +
        //        "Features:\n" +
        //        "- Counts function calls, keywords, and operators\n" +
        //        "- Estimates cycle cost per operation\n" +
        //        "- Groups by category (Texture, Math, etc.)\n" +
        //        "- Calculates total cost per frame\n\n" +
        //        "Note: Cycle costs are approximate and vary by GPU architecture.");
        //    ImGui.EndPopup();
        //}
    }

    private void DrawMainContent()
    {
        // Top section: Settings
        DrawSettingsPanel();

        ImGui.Separator();

        // Main content split
        float availableHeight = ImGui.GetContentRegionAvail().Y;

        // Left panel: Source code
        ImGui.BeginChild("SourcePanel", new Vector2(400, availableHeight), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX);
        DrawSourcePanel();
        ImGui.EndChild();

        ImGui.SameLine();

        // Right panel: Analysis results
        ImGui.BeginChild("ResultsPanel", Vector2.Zero, ImGuiChildFlags.Border);
        DrawResultsPanel();
        ImGui.EndChild();
    }

    private void DrawSettingsPanel()
    {
        ImGui.Text("Invocations per Frame:");
        ImGui.SameLine();

        // Resolution preset dropdown
        ImGui.SetNextItemWidth(200);
        string presetLabel = _selectedResolutionPreset >= 0 && _selectedResolutionPreset < ResolutionPresets.Length
            ? ResolutionPresets[_selectedResolutionPreset].Name
            : "Custom";

        if (ImGui.BeginCombo("##ResolutionPreset", presetLabel))
        {
            for (int i = 0; i < ResolutionPresets.Length; i++)
            {
                bool isSelected = i == _selectedResolutionPreset;
                if (ImGui.Selectable(ResolutionPresets[i].Name, isSelected))
                {
                    _selectedResolutionPreset = i;
                    if (ResolutionPresets[i].Pixels > 0)
                    {
                        _invocationsPerFrame = ResolutionPresets[i].Pixels;
                        ReanalyzeIfNeeded();
                    }
                }
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        // Custom invocation count
        if (_selectedResolutionPreset == ResolutionPresets.Length - 1) // Custom
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputInt("##CustomInvocations", ref _invocationsPerFrame))
            {
                _invocationsPerFrame = Math.Max(1, _invocationsPerFrame);
                ReanalyzeIfNeeded();
            }
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({_invocationsPerFrame:N0} pixels)");
        }

        ImGui.SameLine();
        if (ImGui.Button("Analyze"))
        {
            AnalyzeShader();
        }

        // Error message
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), _errorMessage);
        }
    }

    private void DrawSourcePanel()
    {
        ImGui.Text("Shader Source");

        if (ImGui.Button("Load File"))
            ImGui.OpenPopup("LoadShaderPopup");

        ImGui.SameLine();
        if (ImGui.Button("Paste"))
            PasteFromClipboard();

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            _shaderSource = "";
            _report = null;
            _errorMessage = "";
        }

        ImGui.Separator();

        // Source text input
        Vector2 size = new(-1, -1);
        ImGui.InputTextMultiline("##ShaderSource", ref _shaderSource, 1024 * 1024, size,
            ImGuiInputTextFlags.AllowTabInput);
    }

    private void DrawResultsPanel()
    {
        if (_report == null)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1),
                "Load a shader and click 'Analyze' to see results.");
            return;
        }

        // Summary header
        DrawSummaryHeader();

        ImGui.Separator();

        // Category filter
        ImGui.Text("Filter:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("##CategoryFilter", ref _categoryFilter, 256);

        ImGui.Separator();

        // Results tabs
        if (ImGui.BeginTabBar("ResultsTabs"))
        {
            if (ImGui.BeginTabItem("By Operation"))
            {
                DrawOperationsTable();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("By Category"))
            {
                DrawCategoryBreakdown();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Cost Chart"))
            {
                DrawCostChart();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawSummaryHeader()
    {
        if (_report == null) return;

        // Cost summary boxes
        float boxWidth = 180;
        float boxHeight = 60;

        ImGui.BeginGroup();

        // Per Invocation
        DrawSummaryBox("Cost/Invocation", $"{_report.TotalCostPerInvocation:N0}", "cycles", boxWidth, boxHeight,
            new Vector4(0.2f, 0.4f, 0.8f, 1));

        ImGui.SameLine();

        // Per Frame
        DrawSummaryBox("Cost/Frame", FormatLargeNumber(_report.TotalCostPerFrame), "cycles", boxWidth, boxHeight,
            new Vector4(0.2f, 0.6f, 0.4f, 1));

        ImGui.SameLine();

        // Invocations
        DrawSummaryBox("Invocations", $"{_report.InvocationsPerFrame:N0}", "per frame", boxWidth, boxHeight,
            new Vector4(0.6f, 0.4f, 0.2f, 1));

        ImGui.SameLine();

        // Operations counted
        DrawSummaryBox("Operations", $"{_report.Operations.Count:N0}", "unique", boxWidth, boxHeight,
            new Vector4(0.5f, 0.3f, 0.6f, 1));

        ImGui.EndGroup();
    }

    private static void DrawSummaryBox(string label, string value, string unit, float width, float height, Vector4 color)
    {
        Vector2 pos = ImGui.GetCursorScreenPos();

        // Background
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + new Vector2(width, height),
            ImGui.ColorConvertFloat4ToU32(new Vector4(color.X * 0.3f, color.Y * 0.3f, color.Z * 0.3f, 0.8f)), 4);
        drawList.AddRect(pos, pos + new Vector2(width, height),
            ImGui.ColorConvertFloat4ToU32(color), 4);

        // Content
        ImGui.SetCursorScreenPos(pos + new Vector2(8, 4));
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), label);

        ImGui.SetCursorScreenPos(pos + new Vector2(8, 22));
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.Text(value);
        ImGui.PopStyleColor();

        ImGui.SetCursorScreenPos(pos + new Vector2(8, 42));
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), unit);

        ImGui.SetCursorScreenPos(pos + new Vector2(width + 4, 0));
        ImGui.Dummy(new Vector2(0, height));
    }

    private void DrawOperationsTable()
    {
        if (_report == null) return;

        var operations = _report.Operations
            .Where(op => string.IsNullOrEmpty(_categoryFilter) ||
                         op.Identifier.Contains(_categoryFilter, StringComparison.OrdinalIgnoreCase) ||
                         op.Category.Contains(_categoryFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (_sortByOccurrences)
            operations = [.. operations.OrderByDescending(op => op.Occurrences)];

        if (ImGui.BeginTable("OperationsTable", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable))
        {
            ImGui.TableSetupColumn("Operation", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Cost/Op", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var op in operations)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(op.Identifier);

                ImGui.TableNextColumn();
                ImGui.TextColored(GetCategoryColor(op.Category), op.Category);

                ImGui.TableNextColumn();
                ImGui.Text($"{op.Occurrences}");

                ImGui.TableNextColumn();
                ImGui.Text($"{op.CycleCostPerOccurrence}");

                ImGui.TableNextColumn();
                ImGui.Text($"{op.EstimatedCycles:N0}");
            }

            ImGui.EndTable();
        }
    }

    private void DrawCategoryBreakdown()
    {
        if (_report == null) return;

        var categories = _report.CategoryTotals
            .OrderByDescending(c => c.Value)
            .ToList();

        int totalCycles = _report.TotalCostPerInvocation;

        if (ImGui.BeginTable("CategoryTable", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Cycles", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("% of Total", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Bar", ImGuiTableColumnFlags.WidthFixed, 200);
            ImGui.TableHeadersRow();

            foreach (var (category, cycles) in categories)
            {
                float percentage = totalCycles > 0 ? (float)cycles / totalCycles * 100 : 0;

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextColored(GetCategoryColor(category), category);

                ImGui.TableNextColumn();
                ImGui.Text($"{cycles:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{percentage:F1}%");

                ImGui.TableNextColumn();
                DrawProgressBar(percentage / 100f, GetCategoryColor(category));
            }

            ImGui.EndTable();
        }
    }

    private void DrawCostChart()
    {
        if (_report == null) return;

        var categories = _report.CategoryTotals
            .OrderByDescending(c => c.Value)
            .Take(10)
            .ToList();

        if (categories.Count == 0)
        {
            ImGui.Text("No data to display.");
            return;
        }

        int maxCycles = categories.Max(c => c.Value);
        float barHeight = 25;
        float chartWidth = ImGui.GetContentRegionAvail().X - 150;

        ImGui.Text("Top Categories by Cycle Cost:");
        ImGui.Spacing();

        foreach (var (category, cycles) in categories)
        {
            float barWidth = maxCycles > 0 ? (float)cycles / maxCycles * chartWidth : 0;

            ImGui.Text($"{category,-20}");
            ImGui.SameLine(160);

            Vector2 pos = ImGui.GetCursorScreenPos();
            var drawList = ImGui.GetWindowDrawList();

            // Background bar
            drawList.AddRectFilled(pos, pos + new Vector2(chartWidth, barHeight),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.2f, 1)), 2);

            // Filled bar
            var color = GetCategoryColor(category);
            drawList.AddRectFilled(pos, pos + new Vector2(barWidth, barHeight),
                ImGui.ColorConvertFloat4ToU32(color), 2);

            // Value label
            string label = $"{cycles:N0}";
            drawList.AddText(pos + new Vector2(barWidth + 5, (barHeight - 14) / 2),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), label);

            ImGui.Dummy(new Vector2(chartWidth + 80, barHeight + 4));
        }
    }

    private static void DrawProgressBar(float fraction, Vector4 color)
    {
        Vector2 pos = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        float height = 16;

        var drawList = ImGui.GetWindowDrawList();

        // Background
        drawList.AddRectFilled(pos, pos + new Vector2(width, height),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.2f, 1)), 2);

        // Filled portion
        drawList.AddRectFilled(pos, pos + new Vector2(width * fraction, height),
            ImGui.ColorConvertFloat4ToU32(color), 2);

        ImGui.Dummy(new Vector2(width, height));
    }

    private static Vector4 GetCategoryColor(string category)
    {
        return category switch
        {
            "Texture Sampling" => new Vector4(0.9f, 0.3f, 0.3f, 1),
            "Texture Query" => new Vector4(0.8f, 0.4f, 0.4f, 1),
            "Image Operations" => new Vector4(0.9f, 0.5f, 0.3f, 1),
            "Image Atomics" => new Vector4(1.0f, 0.4f, 0.2f, 1),
            "Trigonometry" => new Vector4(0.3f, 0.7f, 0.9f, 1),
            "Exponential" => new Vector4(0.4f, 0.6f, 0.9f, 1),
            "Geometry" => new Vector4(0.3f, 0.9f, 0.5f, 1),
            "Matrix" => new Vector4(0.5f, 0.8f, 0.4f, 1),
            "Arithmetic" => new Vector4(0.7f, 0.7f, 0.3f, 1),
            "Comparison" => new Vector4(0.6f, 0.6f, 0.4f, 1),
            "Logical" => new Vector4(0.5f, 0.5f, 0.5f, 1),
            "Bitwise" => new Vector4(0.6f, 0.4f, 0.7f, 1),
            "Control Flow" => new Vector4(0.9f, 0.6f, 0.2f, 1),
            "Common" => new Vector4(0.5f, 0.7f, 0.7f, 1),
            "Derivatives" => new Vector4(0.7f, 0.5f, 0.8f, 1),
            "Barriers" => new Vector4(1.0f, 0.3f, 0.5f, 1),
            "Atomics" => new Vector4(0.9f, 0.2f, 0.4f, 1),
            "Noise" => new Vector4(0.4f, 0.8f, 0.8f, 1),
            _ => new Vector4(0.6f, 0.6f, 0.6f, 1)
        };
    }

    private void AnalyzeShader()
    {
        if (string.IsNullOrWhiteSpace(_shaderSource))
        {
            _errorMessage = "No shader source to analyze.";
            _report = null;
            return;
        }

        try
        {
            var options = new GlslCostEstimatorOptions
            {
                InvocationsPerFrame = _invocationsPerFrame
            };

            _report = _estimator.Analyze(_shaderSource, options);
            _errorMessage = "";
        }
        catch (Exception ex)
        {
            _errorMessage = $"Analysis failed: {ex.Message}";
            _report = null;
        }
    }

    private void ReanalyzeIfNeeded()
    {
        if (!string.IsNullOrWhiteSpace(_shaderSource) && _report != null)
        {
            AnalyzeShader();
        }
    }

    private void PasteFromClipboard()
    {
        // Note: ImGui clipboard access might need platform-specific handling
        // For now, we'll use a simple approach
        try
        {
            // This would need proper clipboard integration
            // _shaderSource = ImGui.GetClipboardText() ?? "";
            _errorMessage = "Paste from clipboard - use Ctrl+V in the text area.";
        }
        catch
        {
            _errorMessage = "Failed to paste from clipboard.";
        }
    }

    public void LoadShaderFromPath(string path)
    {
        if (!File.Exists(path))
        {
            _errorMessage = $"File not found: {path}";
            return;
        }

        try
        {
            _shaderSource = File.ReadAllText(path);
            _shaderSourcePath = path;
            _errorMessage = "";
            AnalyzeShader();
        }
        catch (Exception ex)
        {
            _errorMessage = $"Failed to load: {ex.Message}";
        }
    }

    public void LoadShaderSource(string source)
    {
        _shaderSource = source;
        _shaderSourcePath = "";
        _errorMessage = "";
        AnalyzeShader();
    }

    private static string FormatLargeNumber(long number)
    {
        return number switch
        {
            >= 1_000_000_000_000 => $"{number / 1_000_000_000_000.0:F2}T",
            >= 1_000_000_000 => $"{number / 1_000_000_000.0:F2}B",
            >= 1_000_000 => $"{number / 1_000_000.0:F2}M",
            >= 1_000 => $"{number / 1_000.0:F2}K",
            _ => $"{number:N0}"
        };
    }

    /// <summary>
    /// Renders dialog popups. Call this in Render().
    /// </summary>
    public void RenderDialogs()
    {
        // Load Shader popup
        if (ImGui.BeginPopupModal("LoadShaderPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Enter shader file path:");
            ImGui.SetNextItemWidth(500);
            ImGui.InputText("##ShaderPath", ref _shaderSourcePath, 1024);

            if (ImGui.Button("Load") && File.Exists(_shaderSourcePath))
            {
                LoadShaderFromPath(_shaderSourcePath);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        // Export Report popup
        string exportPath = "";
        if (ImGui.BeginPopupModal("ExportReportPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Enter output file path:");
            ImGui.SetNextItemWidth(500);
            ImGui.InputText("##ExportPath", ref exportPath, 1024);

            if (ImGui.Button("Export") && !string.IsNullOrEmpty(exportPath) && _report != null)
            {
                ExportReport(exportPath);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
    }

    private void ExportReport(string path)
    {
        if (_report == null) return;

        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Shader Analysis Report");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine($"- Invocations per Frame: {_report.InvocationsPerFrame:N0}");
            sb.AppendLine($"- Cost per Invocation: {_report.TotalCostPerInvocation:N0} cycles");
            sb.AppendLine($"- Cost per Frame: {_report.TotalCostPerFrame:N0} cycles");
            sb.AppendLine();
            sb.AppendLine("## Category Breakdown");
            sb.AppendLine("| Category | Cycles | % of Total |");
            sb.AppendLine("|----------|--------|------------|");

            foreach (var (category, cycles) in _report.CategoryTotals.OrderByDescending(c => c.Value))
            {
                float pct = _report.TotalCostPerInvocation > 0
                    ? (float)cycles / _report.TotalCostPerInvocation * 100
                    : 0;
                sb.AppendLine($"| {category} | {cycles:N0} | {pct:F1}% |");
            }

            sb.AppendLine();
            sb.AppendLine("## Operations Detail");
            sb.AppendLine("| Operation | Category | Count | Cost/Op | Total |");
            sb.AppendLine("|-----------|----------|-------|---------|-------|");

            foreach (var op in _report.Operations)
            {
                sb.AppendLine($"| {op.Identifier} | {op.Category} | {op.Occurrences} | {op.CycleCostPerOccurrence} | {op.EstimatedCycles:N0} |");
            }

            File.WriteAllText(path, sb.ToString());
            Debug.Out($"Report exported to: {path}");
        }
        catch (Exception ex)
        {
            _errorMessage = $"Export failed: {ex.Message}";
        }
    }
}
