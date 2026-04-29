using ImGuiNET;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor.IMGUI;
using XREngine.Editor.UI;
using XREngine.Rendering;
using EngineDebug = XREngine.Debug;

namespace XREngine.Editor.UI.Tools;

public enum ShaderEditorTab
{
    Diagnostics,
    Analysis,
    Variants,
    CrossCompile,
    Suggestions,
    AI,
    Preview,
    Includes,
    Resolved
}

public sealed class ShaderEditorWindow
{
    private const int SourceCapacity = 1024 * 1024;
    private const int DefaultCompileDebounceMs = 450;
    private const string ShaderFileFilter = "Shader Files|*.glsl;*.shader;*.frag;*.vert;*.geom;*.tesc;*.tese;*.comp;*.task;*.mesh;*.fs;*.vs;*.gs;*.tcs;*.tes;*.cs;*.ts;*.ms|All Files|*.*";
    private const string HlslFileFilter = "HLSL Files|*.hlsl;*.hlsli;*.fx|All Files|*.*";
    private const string SpirvFileFilter = "SPIR-V Files|*.spv|All Files|*.*";

    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly string[] GeneratedSourceChoices = ["Original", "Resolved", "Generated Variant", "Preview Instrumentation"];
    private static ShaderEditorWindow? _instance;

    public static ShaderEditorWindow Instance => _instance ??= new ShaderEditorWindow();
    public static void RenderIfOpen() => _instance?.Render();
    public static void RenderDialogsIfOpen()
    {
        if (_instance?._isOpen == true)
            _instance.RenderDialogs();
    }

    private readonly object _compileLock = new();
    private readonly object _aiLock = new();
    private readonly Dictionary<uint, ShaderEditorSessionSnapshot> _activeSessionUndoScopes = new();
    private readonly GlslCostEstimator _costEstimator = new();
    private ShaderVariantOptimizer _lockingTool = new();

    private bool _isOpen;
    private ShaderEditorTab? _pendingTab;
    private XRShader? _boundShader;
    private TextFile? _boundSource;
    private bool _updatingBoundSource;
    private long _sourceRevision;
    private long _lockingToolRevision = -1;
    private string _shaderSourcePath = string.Empty;
    private string _shaderSource = GetDefaultFragmentShader();
    private string _resolvedSource = string.Empty;
    private int _selectedShaderTypeIndex = (int)EShaderType.Fragment;
    private string _entryPoint = "main";
    private int _compileDebounceMs = DefaultCompileDebounceMs;
    private long _compileDueTicks;
    private int _compileSerial;
    private bool _compileRunning;
    private string _compileStatus = "Not compiled";
    private ShaderEditorCompileResult? _lastCompileResult;
    private IReadOnlyList<ShaderEditorDiagnostic> _diagnostics = [];
    private int _selectedDiagnosticLine;

    private string _completionPrefix = string.Empty;
    private IReadOnlyList<ShaderEditorCompletionItem> _completionItems = [];
    private string _aiInstruction = "Continue the shader from the current cursor context. Return only GLSL code.";
    private string _aiCompletion = string.Empty;
    private string _aiStatus = "Idle";
    private bool _aiRunning;
    private int _aiSerial;

    private int _previewLine = 1;
    private string _previewExpression = "color";
    private string _previewOutputVariable = "FragColor";
    private string _instrumentedPreviewSource = string.Empty;
    private bool _previewInstrumented;

    private int _selectedVariantPresetIndex = (int)ShaderEditorVariantPreset.ShadowCaster;
    private string _customVariantDefine = "XRENGINE_CUSTOM_VARIANT";
    private string _variantSource = string.Empty;
    private string _variantStatus = "No variant generated.";
    private string _variantOutputPath = string.Empty;
    private ShaderEditorVariantResult? _lastVariantResult;
    private bool _compileVariantAfterGenerate = true;
    private string _variantUniformFilter = string.Empty;
    private string _variantNewAnimatedPattern = string.Empty;
    private bool _variantShowOnlyAnimated;
    private bool _variantShowOnlyLocked;
    private int _selectedVariantUniformIndex = -1;

    private int _analysisSourceChoiceIndex;
    private int _analysisInvocationsPerFrame = 1920 * 1080;
    private int _selectedResolutionPreset = 1;
    private string _analysisFilter = string.Empty;
    private bool _analysisSortByOccurrences;
    private string _analysisStatus = "Not analyzed.";
    private ShaderCostReport? _analysisReport;

    private int _crossSourceChoiceIndex;
    private ShaderSourceLanguage _crossSourceLanguage = ShaderSourceLanguage.Glsl;
    private byte[]? _crossSpirvBytes;
    private string _crossHlslSource = string.Empty;
    private string _crossGlslSource = string.Empty;
    private string _crossStatus = "Not cross-compiled.";
    private string _crossOutputBasePath = string.Empty;

    private int _includeSourceKindIndex;
    private IReadOnlyList<string> _engineSnippetNames = [];
    private int _selectedSnippetIndex = -1;
    private IReadOnlyList<ShaderEditorIncludeCandidate> _relativeIncludeCandidates = [];
    private int _selectedRelativeIncludeIndex = -1;
    private string _absoluteIncludePath = string.Empty;
    private string _includePreviewText = string.Empty;
    private string _includePreviewStatus = "Select a snippet or include file.";
    private string _includePreviewDirective = string.Empty;
    private string _includePreviewResolvedPath = string.Empty;
    private long _includeListRevision = -1;

    private static readonly (string Name, int Pixels)[] ResolutionPresets =
    [
        ("720p (1280x720)", 1280 * 720),
        ("1080p (1920x1080)", 1920 * 1080),
        ("1440p (2560x1440)", 2560 * 1440),
        ("4K (3840x2160)", 3840 * 2160),
        ("Custom", -1)
    ];

    private ShaderEditorWindow()
    {
        BindSource(null, new TextFile { Text = _shaderSource });
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => _isOpen = value;
    }

    public void Open()
    {
        _isOpen = true;
        SyncLockingToolFromCurrentSource();
        EnsureCompletionItemsCurrent();
        if (_compileDueTicks == 0 && _lastCompileResult is null)
            ScheduleCompile();
    }

    public void Open(ShaderEditorTab tab)
    {
        _pendingTab = tab;
        Open();
    }

    public void Close() => _isOpen = false;

    public void LoadShader(XRShader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);

        TextFile? source = shader.Source;
        BindSource(shader, source ?? new TextFile { Text = string.Empty, FilePath = shader.FilePath });

        _shaderSourcePath = source?.FilePath ?? shader.FilePath ?? string.Empty;
        _shaderSource = source?.Text ?? string.Empty;
        _selectedShaderTypeIndex = (int)shader.Type;
        _compileStatus = "Loaded from asset. Compile pending.";
        _selectedDiagnosticLine = 0;
        _instrumentedPreviewSource = string.Empty;
        _previewInstrumented = false;
        ResetGeneratedOutputs("Loaded from asset.");
        HandleSourceChanged(updateBoundSource: false);
        Open();
    }

    public void LoadShaderFromPath(string path)
    {
        if (!File.Exists(path))
        {
            EngineDebug.LogError($"Shader file not found: {path}");
            return;
        }

        try
        {
            _shaderSourcePath = Path.GetFullPath(path);
            _shaderSource = File.ReadAllText(_shaderSourcePath);
            BindSource(null, new TextFile { Text = _shaderSource, FilePath = _shaderSourcePath });
            _selectedShaderTypeIndex = (int)XRShader.ResolveType(Path.GetExtension(_shaderSourcePath));
            _compileStatus = "Loaded. Compile pending.";
            _selectedDiagnosticLine = 0;
            _instrumentedPreviewSource = string.Empty;
            _previewInstrumented = false;
            ResetGeneratedOutputs("Loaded from path.");
            HandleSourceChanged(updateBoundSource: false);
            Open();
        }
        catch (Exception ex)
        {
            EngineDebug.LogException(ex, $"Failed to load shader '{path}'.");
        }
    }

    public void Render()
    {
        if (!_isOpen)
            return;

        UpdateDebouncedCompile();

        ImGui.SetNextWindowSize(new Vector2(1280f, 820f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Shader Editor", ref _isOpen, ImGuiWindowFlags.MenuBar))
        {
            DrawMenuBar();
            DrawHeader();
            DrawMainContent();
        }
        ImGui.End();
    }

    public void RenderDialogs()
    {
        ImGuiFileBrowser.DrawDialogs();
    }

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMenuBar())
            return;

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Open Shader..."))
                OpenLoadDialog();

            bool hasPath = !string.IsNullOrWhiteSpace(_shaderSourcePath);
            if (ImGui.MenuItem("Save", null, false, hasPath))
                SaveCurrentShader();

            if (ImGui.MenuItem("Save As..."))
                OpenSaveDialog();

            if (ImGui.MenuItem("Save Generated Variant...", null, false, !string.IsNullOrWhiteSpace(_variantSource)))
                OpenVariantSaveDialog();

            if (ImGui.MenuItem("Reload", null, false, hasPath))
                LoadShaderFromPath(_shaderSourcePath);

            ImGui.Separator();
            if (ImGui.MenuItem("Close"))
                _isOpen = false;

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Build"))
        {
            if (ImGui.MenuItem("Compile Now"))
                StartCompileNow();

            bool canCompilePreview = _previewInstrumented && !string.IsNullOrWhiteSpace(_instrumentedPreviewSource);
            if (ImGui.MenuItem("Compile Preview Instrumentation", null, false, canCompilePreview))
                StartCompileNow(_instrumentedPreviewSource);

            if (ImGui.MenuItem("Generate Selected Variant"))
                ApplyUndoableChange("Generate Shader Variant", GenerateSelectedVariant);

            if (ImGui.MenuItem("Cross-Compile All"))
                ApplyUndoableChange("Cross-Compile All", () => CrossCompileSelectedSource(generateHlsl: true, generateGlsl: true));

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("AI"))
        {
            if (ImGui.MenuItem("Request Completion", null, false, !_aiRunning))
                RequestAiCompletion();

            if (ImGui.MenuItem("Append Completion", null, false, !string.IsNullOrWhiteSpace(_aiCompletion)))
                ApplyUndoableChange("Append AI Shader Completion", AppendAiCompletion, _boundSource);

            ImGui.EndMenu();
        }

        ImGui.EndMenuBar();
    }

    private void DrawHeader()
    {
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(_shaderSourcePath) ? "Untitled shader" : Path.GetFileName(_shaderSourcePath));
        ImGui.SameLine();
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(_shaderSourcePath) ? "No file path" : _shaderSourcePath);

        ImGui.Separator();

        ImGui.TextUnformatted("Type");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        string typePreview = ((EShaderType)_selectedShaderTypeIndex).ToString();
        if (ImGui.BeginCombo("##ShaderEditorType", typePreview))
        {
            foreach (EShaderType type in Enum.GetValues<EShaderType>())
            {
                bool selected = _selectedShaderTypeIndex == (int)type;
                if (ImGui.Selectable(type.ToString(), selected))
                {
                    ApplyUndoableChange("Set Shader Type", () =>
                    {
                        _selectedShaderTypeIndex = (int)type;
                        if (_boundShader is not null)
                            _boundShader.Type = type;
                        HandleSourceMetadataChanged();
                    }, _boundShader);
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("Entry");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputText("##ShaderEditorEntry", ref _entryPoint, 128))
            ScheduleCompile();
        TrackSessionControlUndo("Edit Shader Entry Point");

        ImGui.SameLine();
        ImGui.TextUnformatted("Debounce ms");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt("##ShaderEditorDebounce", ref _compileDebounceMs))
            _compileDebounceMs = Math.Clamp(_compileDebounceMs, 50, 5000);
        TrackSessionControlUndo("Edit Shader Compile Debounce");

        ImGui.SameLine();
        if (ImGui.Button("Compile"))
            StartCompileNow();

        ImGui.SameLine();
        DrawCompileStatusInline();
    }

    private void DrawMainContent()
    {
        float availableHeight = ImGui.GetContentRegionAvail().Y;
        float leftWidth = MathF.Max(520f, ImGui.GetContentRegionAvail().X * 0.54f);

        ImGui.BeginChild("ShaderEditorSourcePane", new Vector2(leftWidth, availableHeight), ImGuiChildFlags.Border);
        DrawSourceEditor();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("ShaderEditorRightPane", Vector2.Zero, ImGuiChildFlags.Border);
        DrawRightPane();
        ImGui.EndChild();
    }

    private void DrawSourceEditor()
    {
        ImGui.TextUnformatted("Source");
        ImGui.SameLine();
        ImGui.TextDisabled(GetSourceMetrics(_shaderSource));

        Vector2 size = new(-1f, -1f);
        if (ImGui.InputTextMultiline("##ShaderEditorSource", ref _shaderSource, SourceCapacity, size, ImGuiInputTextFlags.AllowTabInput))
        {
            SetShaderSourceText(_shaderSource);
        }
        ImGuiUndoHelper.TrackDragUndo("Edit Shader Source", _boundSource);
    }

    private void DrawRightPane()
    {
        if (!ImGui.BeginTabBar("ShaderEditorTabs"))
            return;

        DrawTab(ShaderEditorTab.Diagnostics, "Diagnostics", DrawDiagnosticsTab);
        DrawTab(ShaderEditorTab.Analysis, "Analysis", DrawAnalysisTab);
        DrawTab(ShaderEditorTab.Variants, "Variants", DrawVariantsTab);
        DrawTab(ShaderEditorTab.CrossCompile, "Cross-Compile", DrawCrossCompileTab);
        DrawTab(ShaderEditorTab.Suggestions, "Suggestions", DrawSuggestionsTab);
        DrawTab(ShaderEditorTab.AI, "AI", DrawAiTab);
        DrawTab(ShaderEditorTab.Preview, "Preview", DrawPreviewTab);
        DrawTab(ShaderEditorTab.Includes, "Includes", DrawIncludesTab);
        DrawTab(ShaderEditorTab.Resolved, "Resolved", DrawResolvedSourceTab);

        ImGui.EndTabBar();
    }

    private void DrawTab(ShaderEditorTab tab, string label, Action draw)
    {
        ImGuiTabItemFlags flags = _pendingTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        bool visible;
        if (flags == ImGuiTabItemFlags.None)
        {
            visible = ImGui.BeginTabItem(label);
        }
        else
        {
            bool open = true;
            visible = ImGui.BeginTabItem(label, ref open, flags);
        }

        if (!visible)
            return;

        if (_pendingTab == tab)
            _pendingTab = null;

        draw();
        ImGui.EndTabItem();
    }

    private void DrawDiagnosticsTab()
    {
        ShaderEditorCompileResult? result = _lastCompileResult;
        if (result is not null)
        {
            ImGui.TextUnformatted(result.Success ? "Compiled" : "Failed");
            ImGui.SameLine();
            ImGui.TextDisabled($"{result.Duration.TotalMilliseconds:F1} ms, {result.SpirvByteCount:N0} bytes SPIR-V, {result.CompletedAt:HH:mm:ss}");
        }
        else
        {
            ImGui.TextDisabled("No compile result yet.");
        }

        if (_diagnostics.Count == 0)
            ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), "No diagnostics.");

        if (ImGui.BeginTable("ShaderEditorDiagnostics", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0f, 170f)))
        {
            ImGui.TableSetupColumn("Line", ImGuiTableColumnFlags.WidthFixed, 58f);
            ImGui.TableSetupColumn("Column", ImGuiTableColumnFlags.WidthFixed, 58f);
            ImGui.TableSetupColumn("Severity", ImGuiTableColumnFlags.WidthFixed, 82f);
            ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (ShaderEditorDiagnostic diagnostic in _diagnostics)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable(diagnostic.Line > 0 ? diagnostic.Line.ToString() : "-", _selectedDiagnosticLine == diagnostic.Line, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedDiagnosticLine = diagnostic.Line;
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(diagnostic.Column > 0 ? diagnostic.Column.ToString() : "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(GetDiagnosticColor(diagnostic.Severity), diagnostic.Severity.ToString());
                ImGui.TableNextColumn();
                ImGui.TextWrapped(diagnostic.Message);
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Annotated source");
        DrawAnnotatedSource(string.IsNullOrEmpty(_resolvedSource) ? _shaderSource : _resolvedSource, _diagnostics);
    }

    private void DrawAnalysisTab()
    {
        DrawGeneratedSourceChoice("##ShaderEditorAnalysisSource", ref _analysisSourceChoiceIndex, "Select Analysis Source");

        ImGui.SameLine();
        ImGui.TextUnformatted("Invocations");
        ImGui.SameLine();
        string presetLabel = _selectedResolutionPreset >= 0 && _selectedResolutionPreset < ResolutionPresets.Length
            ? ResolutionPresets[_selectedResolutionPreset].Name
            : "Custom";
        ImGui.SetNextItemWidth(175f);
        if (ImGui.BeginCombo("##ShaderEditorAnalysisResolution", presetLabel))
        {
            for (int i = 0; i < ResolutionPresets.Length; i++)
            {
                bool selected = i == _selectedResolutionPreset;
                if (ImGui.Selectable(ResolutionPresets[i].Name, selected))
                {
                    int presetIndex = i;
                    ApplyUndoableChange("Select Analysis Resolution", () =>
                    {
                        _selectedResolutionPreset = presetIndex;
                        if (ResolutionPresets[presetIndex].Pixels > 0)
                            _analysisInvocationsPerFrame = ResolutionPresets[presetIndex].Pixels;
                    });
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (_selectedResolutionPreset == ResolutionPresets.Length - 1)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f);
            if (ImGui.InputInt("##ShaderEditorAnalysisInvocations", ref _analysisInvocationsPerFrame))
                _analysisInvocationsPerFrame = Math.Max(1, _analysisInvocationsPerFrame);
            TrackSessionControlUndo("Edit Analysis Invocations");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{_analysisInvocationsPerFrame:N0}");
        }

        ImGui.SameLine();
        if (ImGui.Button("Analyze"))
            ApplyUndoableChange("Analyze Shader Source", AnalyzeSelectedSource);

        ImGui.SameLine();
        ImGui.TextDisabled(_analysisStatus);

        if (_analysisReport is null)
        {
            ImGui.Separator();
            ImGui.TextDisabled("No analysis report.");
            return;
        }

        ImGui.Separator();
        DrawAnalysisSummary();

        ImGui.Separator();
        ImGui.TextUnformatted("Filter");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        ImGui.InputText("##ShaderEditorAnalysisFilter", ref _analysisFilter, 256);
        TrackSessionControlUndo("Edit Analysis Filter");

        ImGui.SameLine();
        bool sortByOccurrences = _analysisSortByOccurrences;
        if (ImGui.Checkbox("Sort by count", ref sortByOccurrences))
            ApplyUndoableChange("Toggle Analysis Sort", () => _analysisSortByOccurrences = sortByOccurrences);

        if (!ImGui.BeginTabBar("ShaderEditorAnalysisTabs"))
            return;

        if (ImGui.BeginTabItem("Operations"))
        {
            DrawAnalysisOperationsTable();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Categories"))
        {
            DrawAnalysisCategoryTable();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Chart"))
        {
            DrawAnalysisCostChart();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawVariantsTab()
    {
        SyncLockingToolFromCurrentSource();

        ShaderEditorVariantPreset selectedPreset = (ShaderEditorVariantPreset)_selectedVariantPresetIndex;
        string variantPreview = ShaderEditorServices.GetVariantDisplayName(selectedPreset);
        ImGui.TextUnformatted("Preset");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("##ShaderEditorVariantPreset", variantPreview))
        {
            foreach (ShaderEditorVariantPreset preset in Enum.GetValues<ShaderEditorVariantPreset>())
            {
                bool selected = selectedPreset == preset;
                if (ImGui.Selectable(ShaderEditorServices.GetVariantDisplayName(preset), selected))
                {
                    ShaderEditorVariantPreset nextPreset = preset;
                    ApplyUndoableChange("Select Shader Variant Preset", () =>
                    {
                        _selectedVariantPresetIndex = (int)nextPreset;
                        selectedPreset = nextPreset;
                    });
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (selectedPreset == ShaderEditorVariantPreset.CustomDefine)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220f);
            ImGui.InputText("##ShaderEditorCustomVariantDefine", ref _customVariantDefine, 256);
            TrackSessionControlUndo("Edit Custom Variant Define");
        }

        ImGui.SameLine();
        bool compileVariant = _compileVariantAfterGenerate;
        if (ImGui.Checkbox("Compile", ref compileVariant))
            ApplyUndoableChange("Toggle Compile Generated Variant", () => _compileVariantAfterGenerate = compileVariant);

        ImGui.SameLine();
        if (ImGui.Button("Generate"))
            ApplyUndoableChange("Generate Shader Variant", GenerateSelectedVariant);

        ImGui.SameLine();
        using (new DisabledScope(string.IsNullOrWhiteSpace(_variantSource)))
        {
            if (ImGui.Button("Save"))
                OpenVariantSaveDialog();
        }

        ImGui.SameLine();
        using (new DisabledScope(string.IsNullOrWhiteSpace(_variantSource)))
        {
            if (ImGui.Button("Copy"))
                ImGui.SetClipboardText(_variantSource);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(_variantStatus);

        if (selectedPreset == ShaderEditorVariantPreset.OptimizedLocked)
        {
            ImGui.Separator();
            DrawLockedVariantControls();
        }

        ImGui.Separator();
        string generated = _variantSource;
        ImGui.InputTextMultiline("##ShaderEditorVariantSource", ref generated, SourceCapacity, Vector2.Zero, ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
    }

    private void DrawCrossCompileTab()
    {
        DrawGeneratedSourceChoice("##ShaderEditorCrossSource", ref _crossSourceChoiceIndex, "Select Cross-Compile Source");

        ImGui.SameLine();
        ImGui.TextUnformatted("Language");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        if (ImGui.BeginCombo("##ShaderEditorCrossLanguage", _crossSourceLanguage.ToString()))
        {
            foreach (ShaderSourceLanguage language in Enum.GetValues<ShaderSourceLanguage>())
            {
                bool selected = _crossSourceLanguage == language;
                if (ImGui.Selectable(language.ToString(), selected))
                {
                    ShaderSourceLanguage nextLanguage = language;
                    ApplyUndoableChange("Select Cross-Compile Language", () => _crossSourceLanguage = nextLanguage);
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (ImGui.Button("SPIR-V"))
            ApplyUndoableChange("Cross-Compile SPIR-V", () => CrossCompileSelectedSource(generateHlsl: false, generateGlsl: false));

        ImGui.SameLine();
        if (ImGui.Button("HLSL"))
            ApplyUndoableChange("Cross-Compile HLSL", () => CrossCompileSelectedSource(generateHlsl: true, generateGlsl: false));

        ImGui.SameLine();
        if (ImGui.Button("GLSL"))
            ApplyUndoableChange("Cross-Compile GLSL", () => CrossCompileSelectedSource(generateHlsl: false, generateGlsl: true));

        ImGui.SameLine();
        if (ImGui.Button("All"))
            ApplyUndoableChange("Cross-Compile All", () => CrossCompileSelectedSource(generateHlsl: true, generateGlsl: true));

        ImGui.SameLine();
        using (new DisabledScope(_crossSpirvBytes is not { Length: > 0 }))
        {
            if (ImGui.Button("Save SPIR-V"))
                OpenCrossSpirvSaveDialog();
        }

        ImGui.SameLine();
        using (new DisabledScope(string.IsNullOrWhiteSpace(_crossHlslSource)))
        {
            if (ImGui.Button("Save HLSL"))
                OpenCrossTextSaveDialog("ShaderEditorSaveCrossHlsl", "Save HLSL", ".hlsl", HlslFileFilter, _crossHlslSource);
        }

        ImGui.SameLine();
        using (new DisabledScope(string.IsNullOrWhiteSpace(_crossGlslSource)))
        {
            if (ImGui.Button("Save GLSL"))
                OpenCrossTextSaveDialog("ShaderEditorSaveCrossGlsl", "Save GLSL", ".glsl", ShaderFileFilter, _crossGlslSource);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(_crossStatus);

        ImGui.Separator();
        if (_crossSpirvBytes is { Length: > 0 })
            ImGui.TextUnformatted($"SPIR-V: {_crossSpirvBytes.Length:N0} bytes");
        else
            ImGui.TextDisabled("SPIR-V not generated.");

        if (ImGui.BeginTabBar("ShaderEditorCrossOutputTabs"))
        {
            if (ImGui.BeginTabItem("HLSL"))
            {
                string hlsl = _crossHlslSource;
                ImGui.InputTextMultiline("##ShaderEditorCrossHlsl", ref hlsl, SourceCapacity, Vector2.Zero, ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("GLSL"))
            {
                string glsl = _crossGlslSource;
                ImGui.InputTextMultiline("##ShaderEditorCrossGlsl", ref glsl, SourceCapacity, Vector2.Zero, ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawLockedVariantControls()
    {
        bool optimizeDeadCode = _lockingTool.OptimizeDeadCode;
        if (ImGui.Checkbox("Dead code", ref optimizeDeadCode))
            ApplyUndoableChange("Toggle Shader Dead Code Optimization", () => _lockingTool.OptimizeDeadCode = optimizeDeadCode, _lockingTool);

        ImGui.SameLine();
        bool evaluateConstants = _lockingTool.EvaluateConstantExpressions;
        if (ImGui.Checkbox("Const eval", ref evaluateConstants))
            ApplyUndoableChange("Toggle Shader Constant Evaluation", () => _lockingTool.EvaluateConstantExpressions = evaluateConstants, _lockingTool);

        ImGui.SameLine();
        bool removeUnusedUniforms = _lockingTool.RemoveUnusedUniforms;
        if (ImGui.Checkbox("Unused uniforms", ref removeUnusedUniforms))
            ApplyUndoableChange("Toggle Shader Unused Uniform Removal", () => _lockingTool.RemoveUnusedUniforms = removeUnusedUniforms, _lockingTool);

        ImGui.SameLine();
        bool inlineSingleUse = _lockingTool.InlineSingleUseConstants;
        if (ImGui.Checkbox("Inline const", ref inlineSingleUse))
            ApplyUndoableChange("Toggle Shader Constant Inlining", () => _lockingTool.InlineSingleUseConstants = inlineSingleUse, _lockingTool);

        ImGui.Separator();
        ImGui.TextUnformatted("Animated patterns");
        ImGui.BeginChild("ShaderEditorVariantPatterns", new Vector2(0f, 78f), ImGuiChildFlags.Border);
        foreach (string pattern in _lockingTool.GetAnimatedPatterns())
        {
            ImGui.PushID(pattern);
            ImGui.TextUnformatted(pattern);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                string patternToRemove = pattern;
                ApplyUndoableChange("Remove Animated Uniform Pattern", () => _lockingTool.RemoveAnimatedPattern(patternToRemove), _lockingTool);
            }
            ImGui.PopID();
        }
        ImGui.EndChild();

        ImGui.SetNextItemWidth(MathF.Max(180f, ImGui.GetContentRegionAvail().X - 60f));
        ImGui.InputText("##ShaderEditorVariantPattern", ref _variantNewAnimatedPattern, 256);
        TrackSessionControlUndo("Edit Animated Uniform Pattern");
        ImGui.SameLine();
        if (ImGui.Button("Add") && !string.IsNullOrWhiteSpace(_variantNewAnimatedPattern))
        {
            string pattern = _variantNewAnimatedPattern;
            ApplyUndoableChange("Add Animated Uniform Pattern", () =>
            {
                _lockingTool.AddAnimatedPattern(pattern);
                _variantNewAnimatedPattern = string.Empty;
            }, _lockingTool);
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Uniforms");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f);
        ImGui.InputText("##ShaderEditorVariantUniformFilter", ref _variantUniformFilter, 256);
        TrackSessionControlUndo("Edit Variant Uniform Filter");
        ImGui.SameLine();
        bool showOnlyAnimated = _variantShowOnlyAnimated;
        if (ImGui.Checkbox("Animated", ref showOnlyAnimated))
            ApplyUndoableChange("Toggle Animated Uniform Filter", () => _variantShowOnlyAnimated = showOnlyAnimated);
        ImGui.SameLine();
        bool showOnlyLocked = _variantShowOnlyLocked;
        if (ImGui.Checkbox("Locked", ref showOnlyLocked))
            ApplyUndoableChange("Toggle Locked Uniform Filter", () => _variantShowOnlyLocked = showOnlyLocked);

        IReadOnlyDictionary<string, UniformLockInfo> uniforms = _lockingTool.GetUniforms();
        if (ImGui.BeginTable("ShaderEditorVariantUniforms", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0f, 160f)))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Animated", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableHeadersRow();

            int index = 0;
            foreach (UniformLockInfo info in uniforms.Values)
            {
                if (!ShouldShowUniform(info))
                {
                    index++;
                    continue;
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable(info.Name, _selectedVariantUniformIndex == index, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedVariantUniformIndex = index;

                ImGui.TableNextColumn();
                ImGui.TextDisabled(info.TypeString ?? info.Type.ToString());
                ImGui.TableNextColumn();
                ImGui.TextDisabled(info.Value?.ToString() ?? string.Empty);
                ImGui.TableNextColumn();
                bool animated = info.IsAnimated;
                ImGui.PushID(index);
                if (ImGui.Checkbox("##Animated", ref animated))
                {
                    string uniformName = info.Name;
                    bool nextAnimated = animated;
                    ApplyUndoableChange("Toggle Shader Uniform Animation", () => _lockingTool.SetUniformAnimated(uniformName, nextAnimated), _lockingTool);
                }
                ImGui.PopID();

                index++;
            }

            ImGui.EndTable();
        }

        string preview = _lockingTool.GetPreviewText();
        ImGui.TextDisabled($"{Math.Max(1, CountLines(preview))} preview lines");
    }

    private bool ShouldShowUniform(UniformLockInfo info)
    {
        if (!string.IsNullOrWhiteSpace(_variantUniformFilter) &&
            !info.Name.Contains(_variantUniformFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (_variantShowOnlyAnimated && !info.IsAnimated)
            return false;

        if (_variantShowOnlyLocked && info.IsAnimated)
            return false;

        return true;
    }

    private void DrawSuggestionsTab()
    {
        string trailing = ShaderEditorServices.GetTrailingIdentifierToken(_shaderSource);
        if (string.IsNullOrWhiteSpace(_completionPrefix) && !string.IsNullOrWhiteSpace(trailing))
            _completionPrefix = trailing;

        ImGui.TextUnformatted("Prefix");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##ShaderEditorCompletionPrefix", ref _completionPrefix, 128);
        TrackSessionControlUndo("Edit Completion Prefix");

        ImGui.Separator();

        IEnumerable<ShaderEditorCompletionItem> filtered = _completionItems;
        if (!string.IsNullOrWhiteSpace(_completionPrefix))
        {
            filtered = filtered.Where(item => item.Name.StartsWith(_completionPrefix, StringComparison.OrdinalIgnoreCase));
        }

        if (ImGui.BeginTable("ShaderEditorCompletions", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, Vector2.Zero))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (ShaderEditorCompletionItem item in filtered.Take(250))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable(item.Name, false, ImGuiSelectableFlags.SpanAllColumns))
                {
                    string completion = item.Name;
                    ApplyUndoableChange("Insert Shader Completion", () =>
                    {
                        SetShaderSourceText(ShaderEditorServices.InsertCompletionAtEnd(_shaderSource, _completionPrefix, completion));
                        _completionPrefix = string.Empty;
                    }, _boundSource);
                }
                ImGui.TableNextColumn();
                ImGui.TextDisabled(item.Kind);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(item.Detail);
            }

            ImGui.EndTable();
        }
    }

    private void DrawAiTab()
    {
        string model = Engine.EditorPreferences?.McpAssistantOpenAiModel ?? "gpt-5-codex";
        bool hasKey = !string.IsNullOrWhiteSpace(ResolveOpenAiApiKey());

        ImGui.TextUnformatted("OpenAI model");
        ImGui.SameLine();
        ImGui.TextDisabled(model);
        ImGui.SameLine();
        ImGui.TextColored(hasKey ? new Vector4(0.45f, 0.85f, 0.45f, 1f) : new Vector4(0.95f, 0.55f, 0.35f, 1f), hasKey ? "key ready" : "key missing");

        ImGui.TextUnformatted("Instruction");
        ImGui.InputTextMultiline("##ShaderEditorAiInstruction", ref _aiInstruction, 4096, new Vector2(-1f, 90f), ImGuiInputTextFlags.AllowTabInput);
        TrackSessionControlUndo("Edit AI Shader Instruction");

        using (new DisabledScope(_aiRunning || !hasKey))
        {
            if (ImGui.Button("Request Completion"))
                ApplyUndoableChange("Request Shader AI Completion", RequestAiCompletion);
        }

        ImGui.SameLine();
        using (new DisabledScope(string.IsNullOrWhiteSpace(_aiCompletion)))
        {
            if (ImGui.Button("Append"))
                ApplyUndoableChange("Append AI Shader Completion", AppendAiCompletion, _boundSource);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(_aiStatus);

        ImGui.Separator();
        ImGui.TextUnformatted("Completion");
        ImGui.InputTextMultiline("##ShaderEditorAiCompletion", ref _aiCompletion, SourceCapacity, new Vector2(-1f, -1f), ImGuiInputTextFlags.AllowTabInput);
        TrackSessionControlUndo("Edit AI Shader Completion");
    }

    private void DrawPreviewTab()
    {
        int maxLine = Math.Max(1, CountLines(_shaderSource));
        _previewLine = Math.Clamp(_previewLine, 1, maxLine);

        ImGui.TextUnformatted("Line");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        ImGui.InputInt("##ShaderEditorPreviewLine", ref _previewLine);
        _previewLine = Math.Clamp(_previewLine, 1, maxLine);
        TrackSessionControlUndo("Edit Shader Preview Line");

        ImGui.SameLine();
        ImGui.TextUnformatted("Output");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        ImGui.InputText("##ShaderEditorPreviewOutput", ref _previewOutputVariable, 128);
        TrackSessionControlUndo("Edit Shader Preview Output");

        ImGui.TextUnformatted("Expression");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##ShaderEditorPreviewExpression", ref _previewExpression, 512);
        TrackSessionControlUndo("Edit Shader Preview Expression");

        if (ImGui.Button("Build Variable Preview"))
        {
            ApplyUndoableChange("Build Shader Variable Preview", () =>
            {
                _instrumentedPreviewSource = ShaderEditorServices.BuildInstrumentedPreviewSource(
                    _shaderSource,
                    _previewLine,
                    _previewExpression,
                    _previewOutputVariable);
                _previewInstrumented = !string.Equals(_instrumentedPreviewSource, _shaderSource, StringComparison.Ordinal);
            });
        }

        ImGui.SameLine();
        using (new DisabledScope(!_previewInstrumented))
        {
            if (ImGui.Button("Compile Preview"))
                StartCompileNow(_instrumentedPreviewSource);
        }

        ImGui.Separator();
        ImGui.TextDisabled("Variable preview instruments the shader so the selected expression is written to the fragment output and returned at the chosen line. The image render target hookup is intentionally separate from this source transform.");
        ImGui.InputTextMultiline("##ShaderEditorPreviewSource", ref _instrumentedPreviewSource, SourceCapacity, new Vector2(-1f, -1f), ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
    }

    private void DrawIncludesTab()
    {
        if (RefreshIncludeCatalogs(force: false))
            RefreshIncludePreview();

        ShaderEditorIncludeSourceKind sourceKind = (ShaderEditorIncludeSourceKind)Math.Clamp(
            _includeSourceKindIndex,
            0,
            Enum.GetValues<ShaderEditorIncludeSourceKind>().Length - 1);

        ImGui.TextUnformatted("Source");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(155f);
        if (ImGui.BeginCombo("##ShaderEditorIncludeSourceKind", GetIncludeSourceKindLabel(sourceKind)))
        {
            foreach (ShaderEditorIncludeSourceKind kind in Enum.GetValues<ShaderEditorIncludeSourceKind>())
            {
                bool selected = sourceKind == kind;
                if (ImGui.Selectable(GetIncludeSourceKindLabel(kind), selected))
                {
                    ShaderEditorIncludeSourceKind nextKind = kind;
                    ApplyUndoableChange("Select Shader Include Source", () =>
                    {
                        _includeSourceKindIndex = (int)nextKind;
                        RefreshIncludePreview();
                    });
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
        {
            ApplyUndoableChange("Refresh Shader Include Catalog", () =>
            {
                RefreshIncludeCatalogs(force: true);
                RefreshIncludePreview();
            });
        }

        ImGui.SameLine();
        using (new DisabledScope(string.IsNullOrWhiteSpace(_includePreviewDirective)))
        {
            if (ImGui.Button("Insert"))
            {
                string directive = _includePreviewDirective;
                ApplyUndoableChange("Insert Shader Include Directive", () =>
                    SetShaderSourceText(ShaderEditorServices.AppendDirective(_shaderSource, directive)), _boundSource);
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled(_includePreviewStatus);

        ImGui.Separator();
        switch (sourceKind)
        {
            case ShaderEditorIncludeSourceKind.EngineSnippet:
                DrawEngineSnippetSelector();
                break;
            case ShaderEditorIncludeSourceKind.RelativeInclude:
                DrawRelativeIncludeSelector();
                break;
            case ShaderEditorIncludeSourceKind.AbsoluteInclude:
                DrawAbsoluteIncludeSelector();
                break;
        }

        if (!string.IsNullOrWhiteSpace(_includePreviewDirective))
        {
            ImGui.TextUnformatted("Directive");
            ImGui.SameLine();
            ImGui.TextDisabled(_includePreviewDirective);
        }

        if (!string.IsNullOrWhiteSpace(_includePreviewResolvedPath))
        {
            ImGui.TextUnformatted("Resolved");
            ImGui.SameLine();
            ImGui.TextDisabled(_includePreviewResolvedPath);
        }

        ImGui.TextUnformatted("Preview");
        string preview = _includePreviewText;
        using (new DisabledScope(true))
        {
            ImGui.InputTextMultiline(
                "##ShaderEditorIncludePreview",
                ref preview,
                SourceCapacity,
                new Vector2(-1f, -1f),
                ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
        }
    }

    private void DrawEngineSnippetSelector()
    {
        string preview = _selectedSnippetIndex >= 0 && _selectedSnippetIndex < _engineSnippetNames.Count
            ? _engineSnippetNames[_selectedSnippetIndex]
            : "Select snippet";

        ImGui.TextUnformatted("Snippet");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo("##ShaderEditorSnippetSelector", preview))
            return;

        for (int i = 0; i < _engineSnippetNames.Count; i++)
        {
            bool selected = i == _selectedSnippetIndex;
            if (ImGui.Selectable(_engineSnippetNames[i], selected))
            {
                int snippetIndex = i;
                ApplyUndoableChange("Select Engine Shader Snippet", () =>
                {
                    _selectedSnippetIndex = snippetIndex;
                    RefreshIncludePreview();
                });
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawRelativeIncludeSelector()
    {
        string preview = _selectedRelativeIncludeIndex >= 0 && _selectedRelativeIncludeIndex < _relativeIncludeCandidates.Count
            ? _relativeIncludeCandidates[_selectedRelativeIncludeIndex].DisplayName
            : "Select include";

        ImGui.TextUnformatted("Include");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo("##ShaderEditorRelativeIncludeSelector", preview))
            return;

        for (int i = 0; i < _relativeIncludeCandidates.Count; i++)
        {
            ShaderEditorIncludeCandidate candidate = _relativeIncludeCandidates[i];
            bool selected = i == _selectedRelativeIncludeIndex;
            if (ImGui.Selectable(candidate.DisplayName, selected))
            {
                int includeIndex = i;
                ApplyUndoableChange("Select Relative Shader Include", () =>
                {
                    _selectedRelativeIncludeIndex = includeIndex;
                    RefreshIncludePreview();
                });
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawAbsoluteIncludeSelector()
    {
        ImGui.TextUnformatted("Path");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Max(120f, ImGui.GetContentRegionAvail().X - 82f));
        if (ImGui.InputText("##ShaderEditorAbsoluteIncludePath", ref _absoluteIncludePath, 1024))
            RefreshIncludePreview();
        TrackSessionControlUndo("Edit Absolute Shader Include Path");

        ImGui.SameLine();
        if (ImGui.Button("Browse"))
            OpenAbsoluteIncludeDialog();
    }

    private void DrawResolvedSourceTab()
    {
        string resolved = string.IsNullOrWhiteSpace(_resolvedSource) ? _shaderSource : _resolvedSource;
        ImGui.InputTextMultiline("##ShaderEditorResolvedSource", ref resolved, SourceCapacity, Vector2.Zero, ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
    }

    private void DrawGeneratedSourceChoice(string id, ref int selectedIndex, string undoDescription)
    {
        selectedIndex = Math.Clamp(selectedIndex, 0, GeneratedSourceChoices.Length - 1);
        ImGui.TextUnformatted("Source");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f);
        if (!ImGui.BeginCombo(id, GeneratedSourceChoices[selectedIndex]))
            return;

        for (int i = 0; i < GeneratedSourceChoices.Length; i++)
        {
            bool selected = selectedIndex == i;
            bool enabled = !string.IsNullOrWhiteSpace(GetGeneratedSourceByIndex(i));
            using (new DisabledScope(!enabled))
            {
                if (ImGui.Selectable(GeneratedSourceChoices[i], selected, enabled ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled))
                {
                    ShaderEditorSessionSnapshot before = CaptureSessionSnapshot();
                    selectedIndex = i;
                    RecordSessionSnapshotChange(undoDescription, before, CaptureSessionSnapshot());
                }
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private string GetSelectedGeneratedSource(int selectedIndex)
    {
        selectedIndex = Math.Clamp(selectedIndex, 0, GeneratedSourceChoices.Length - 1);
        string source = GetGeneratedSourceByIndex(selectedIndex);
        return string.IsNullOrWhiteSpace(source) ? _shaderSource : source;
    }

    private string GetGeneratedSourceByIndex(int selectedIndex)
        => selectedIndex switch
        {
            0 => _shaderSource,
            1 => string.IsNullOrWhiteSpace(_resolvedSource) ? _shaderSource : _resolvedSource,
            2 => _variantSource,
            3 => _instrumentedPreviewSource,
            _ => _shaderSource
        };

    private void AnalyzeSelectedSource()
    {
        string source = GetSelectedGeneratedSource(_analysisSourceChoiceIndex);
        if (string.IsNullOrWhiteSpace(source))
        {
            _analysisReport = null;
            _analysisStatus = "No source.";
            return;
        }

        try
        {
            var options = new GlslCostEstimatorOptions
            {
                InvocationsPerFrame = Math.Max(1, _analysisInvocationsPerFrame)
            };

            _analysisReport = _costEstimator.Analyze(source, options);
            _analysisStatus = "Analysis ready.";
        }
        catch (Exception ex)
        {
            _analysisReport = null;
            _analysisStatus = ex.Message;
        }
    }

    private void DrawAnalysisSummary()
    {
        if (_analysisReport is null)
            return;

        float boxWidth = MathF.Max(130f, (ImGui.GetContentRegionAvail().X - 18f) / 4f);
        DrawSummaryBox("Cost/Invocation", $"{_analysisReport.TotalCostPerInvocation:N0}", "cycles", boxWidth, 58f, new Vector4(0.28f, 0.56f, 0.9f, 1f));
        ImGui.SameLine();
        DrawSummaryBox("Cost/Frame", FormatLargeNumber(_analysisReport.TotalCostPerFrame), "cycles", boxWidth, 58f, new Vector4(0.35f, 0.72f, 0.48f, 1f));
        ImGui.SameLine();
        DrawSummaryBox("Invocations", $"{_analysisReport.InvocationsPerFrame:N0}", "per frame", boxWidth, 58f, new Vector4(0.78f, 0.58f, 0.32f, 1f));
        ImGui.SameLine();
        DrawSummaryBox("Operations", $"{_analysisReport.Operations.Count:N0}", "unique", boxWidth, 58f, new Vector4(0.62f, 0.48f, 0.8f, 1f));
    }

    private static void DrawSummaryBox(string label, string value, string unit, float width, float height, Vector4 color)
    {
        Vector2 pos = ImGui.GetCursorScreenPos();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + new Vector2(width, height),
            ImGui.ColorConvertFloat4ToU32(new Vector4(color.X * 0.22f, color.Y * 0.22f, color.Z * 0.22f, 0.82f)), 4f);
        drawList.AddRect(pos, pos + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(color), 4f);

        ImGui.SetCursorScreenPos(pos + new Vector2(8f, 4f));
        ImGui.TextDisabled(label);
        ImGui.SetCursorScreenPos(pos + new Vector2(8f, 21f));
        ImGui.TextColored(color, value);
        ImGui.SetCursorScreenPos(pos + new Vector2(8f, 40f));
        ImGui.TextDisabled(unit);
        ImGui.SetCursorScreenPos(pos + new Vector2(width + 4f, pos.Y - pos.Y));
        ImGui.Dummy(new Vector2(0f, height));
    }

    private void DrawAnalysisOperationsTable()
    {
        if (_analysisReport is null)
            return;

        List<OperationResult> operations = _analysisReport.Operations
            .Where(op => string.IsNullOrWhiteSpace(_analysisFilter) ||
                         op.Identifier.Contains(_analysisFilter, StringComparison.OrdinalIgnoreCase) ||
                         op.Category.Contains(_analysisFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (_analysisSortByOccurrences)
            operations.Sort((left, right) => right.Occurrences.CompareTo(left.Occurrences));

        if (!ImGui.BeginTable("ShaderEditorAnalysisOperations", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, Vector2.Zero))
            return;

        ImGui.TableSetupColumn("Operation", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 125f);
        ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableHeadersRow();

        foreach (OperationResult operation in operations)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(operation.Identifier);
            ImGui.TableNextColumn();
            ImGui.TextColored(GetCategoryColor(operation.Category), operation.Category);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(operation.Occurrences.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(operation.CycleCostPerOccurrence.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{operation.EstimatedCycles:N0}");
        }

        ImGui.EndTable();
    }

    private void DrawAnalysisCategoryTable()
    {
        if (_analysisReport is null)
            return;

        int totalCycles = _analysisReport.TotalCostPerInvocation;
        if (!ImGui.BeginTable("ShaderEditorAnalysisCategories", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable, Vector2.Zero))
            return;

        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Cycles", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("%", ImGuiTableColumnFlags.WidthFixed, 55f);
        ImGui.TableSetupColumn("Bar", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach ((string category, int cycles) in _analysisReport.CategoryTotals.OrderByDescending(pair => pair.Value))
        {
            float fraction = totalCycles > 0 ? (float)cycles / totalCycles : 0f;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(GetCategoryColor(category), category);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{cycles:N0}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{fraction * 100f:F1}");
            ImGui.TableNextColumn();
            DrawProgressBar(fraction, GetCategoryColor(category));
        }

        ImGui.EndTable();
    }

    private void DrawAnalysisCostChart()
    {
        if (_analysisReport is null)
            return;

        List<KeyValuePair<string, int>> categories = _analysisReport.CategoryTotals
            .OrderByDescending(pair => pair.Value)
            .Take(10)
            .ToList();
        if (categories.Count == 0)
        {
            ImGui.TextDisabled("No category data.");
            return;
        }

        int maxCycles = categories.Max(pair => pair.Value);
        float chartWidth = MathF.Max(120f, ImGui.GetContentRegionAvail().X - 150f);
        foreach ((string category, int cycles) in categories)
        {
            float fraction = maxCycles > 0 ? (float)cycles / maxCycles : 0f;
            ImGui.TextUnformatted(category);
            ImGui.SameLine(150f);
            Vector2 pos = ImGui.GetCursorScreenPos();
            Vector4 color = GetCategoryColor(category);
            ImDrawListPtr drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(pos, pos + new Vector2(chartWidth, 20f), ImGui.ColorConvertFloat4ToU32(new Vector4(0.16f, 0.16f, 0.16f, 1f)), 2f);
            drawList.AddRectFilled(pos, pos + new Vector2(chartWidth * fraction, 20f), ImGui.ColorConvertFloat4ToU32(color), 2f);
            drawList.AddText(pos + new Vector2(chartWidth * fraction + 5f, 2f), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), $"{cycles:N0}");
            ImGui.Dummy(new Vector2(chartWidth + 80f, 24f));
        }
    }

    private static void DrawProgressBar(float fraction, Vector4 color)
    {
        Vector2 pos = ImGui.GetCursorScreenPos();
        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + new Vector2(width, 16f), ImGui.ColorConvertFloat4ToU32(new Vector4(0.16f, 0.16f, 0.16f, 1f)), 2f);
        drawList.AddRectFilled(pos, pos + new Vector2(width * Math.Clamp(fraction, 0f, 1f), 16f), ImGui.ColorConvertFloat4ToU32(color), 2f);
        ImGui.Dummy(new Vector2(width, 16f));
    }

    private static Vector4 GetCategoryColor(string category)
        => category switch
        {
            "Texture Sampling" => new Vector4(0.9f, 0.3f, 0.3f, 1f),
            "Texture Query" => new Vector4(0.8f, 0.4f, 0.4f, 1f),
            "Image Operations" => new Vector4(0.9f, 0.5f, 0.3f, 1f),
            "Image Atomics" => new Vector4(1.0f, 0.4f, 0.2f, 1f),
            "Trigonometry" => new Vector4(0.3f, 0.7f, 0.9f, 1f),
            "Exponential" => new Vector4(0.4f, 0.6f, 0.9f, 1f),
            "Geometry" => new Vector4(0.3f, 0.9f, 0.5f, 1f),
            "Matrix" => new Vector4(0.5f, 0.8f, 0.4f, 1f),
            "Arithmetic" => new Vector4(0.7f, 0.7f, 0.3f, 1f),
            "Comparison" => new Vector4(0.6f, 0.6f, 0.4f, 1f),
            "Logical" => new Vector4(0.5f, 0.5f, 0.5f, 1f),
            "Bitwise" => new Vector4(0.6f, 0.4f, 0.7f, 1f),
            "Control Flow" => new Vector4(0.9f, 0.6f, 0.2f, 1f),
            "Common" => new Vector4(0.5f, 0.7f, 0.7f, 1f),
            "Derivatives" => new Vector4(0.7f, 0.5f, 0.8f, 1f),
            "Barriers" => new Vector4(1.0f, 0.3f, 0.5f, 1f),
            "Atomics" => new Vector4(0.9f, 0.2f, 0.4f, 1f),
            "Noise" => new Vector4(0.4f, 0.8f, 0.8f, 1f),
            _ => new Vector4(0.6f, 0.6f, 0.6f, 1f)
        };

    private static string FormatLargeNumber(long number)
        => number switch
        {
            >= 1_000_000_000_000 => $"{number / 1_000_000_000_000.0:F2}T",
            >= 1_000_000_000 => $"{number / 1_000_000_000.0:F2}B",
            >= 1_000_000 => $"{number / 1_000_000.0:F2}M",
            >= 1_000 => $"{number / 1_000.0:F2}K",
            _ => $"{number:N0}"
        };

    private void GenerateSelectedVariant()
    {
        SyncLockingToolFromCurrentSource();

        ShaderEditorVariantPreset preset = (ShaderEditorVariantPreset)_selectedVariantPresetIndex;
        string? lockedSource = preset == ShaderEditorVariantPreset.OptimizedLocked
            ? _lockingTool.GetPreviewText()
            : null;

        _lastVariantResult = ShaderEditorServices.GenerateVariant(
            _shaderSource,
            _shaderSourcePath,
            (EShaderType)_selectedShaderTypeIndex,
            preset,
            _customVariantDefine,
            lockedSource);

        _variantStatus = _lastVariantResult.Message;
        _variantSource = _lastVariantResult.Success ? _lastVariantResult.Source : string.Empty;
        if (_lastVariantResult.Success)
        {
            _variantOutputPath = ShaderEditorServices.SuggestVariantPath(_shaderSourcePath, _lastVariantResult.Name);
            _crossSourceChoiceIndex = 2;
            _analysisSourceChoiceIndex = 2;

            if (_compileVariantAfterGenerate)
                StartCompileNow(_variantSource, "variant");
        }
    }

    private void CrossCompileSelectedSource(bool generateHlsl, bool generateGlsl)
    {
        string source = GetSelectedGeneratedSource(_crossSourceChoiceIndex);
        if (string.IsNullOrWhiteSpace(source))
        {
            _crossStatus = "No source.";
            return;
        }

        ShaderEditorCrossCompileResult result = ShaderEditorServices.CrossCompile(
            source,
            _shaderSourcePath,
            (EShaderType)_selectedShaderTypeIndex,
            _crossSourceLanguage,
            _entryPoint,
            generateHlsl,
            generateGlsl);

        if (result.Success)
        {
            _crossSpirvBytes = result.SpirvBytes;
            if (generateHlsl)
                _crossHlslSource = result.HlslSource;
            if (generateGlsl)
                _crossGlslSource = result.GlslSource;

            _crossOutputBasePath = Path.ChangeExtension(
                ShaderEditorServices.SuggestVariantPath(_shaderSourcePath, GeneratedSourceChoices[Math.Clamp(_crossSourceChoiceIndex, 0, GeneratedSourceChoices.Length - 1)], string.Empty),
                null) ?? string.Empty;
            _crossStatus = $"OK ({result.Duration.TotalMilliseconds:F0} ms)";
        }
        else
        {
            _crossStatus = result.Message;
            if (!generateHlsl && !generateGlsl)
                _crossSpirvBytes = null;
        }
    }

    private void OpenVariantSaveDialog()
    {
        string path = string.IsNullOrWhiteSpace(_variantOutputPath)
            ? ShaderEditorServices.SuggestVariantPath(_shaderSourcePath, _lastVariantResult?.Name ?? "variant")
            : _variantOutputPath;
        string directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        string fileName = Path.GetFileName(path);

        ImGuiFileBrowser.SaveFile("ShaderEditorSaveVariant", "Save Shader Variant", result =>
        {
            if (result.Success && !string.IsNullOrWhiteSpace(result.SelectedPath))
            {
                SaveTextFile(result.SelectedPath, _variantSource);
                _variantOutputPath = Path.GetFullPath(result.SelectedPath);
                _variantStatus = $"Saved {Path.GetFileName(_variantOutputPath)}.";
            }
        }, ShaderFileFilter, directory, fileName);
    }

    private void OpenCrossSpirvSaveDialog()
    {
        string basePath = string.IsNullOrWhiteSpace(_crossOutputBasePath)
            ? ShaderEditorServices.SuggestVariantPath(_shaderSourcePath, "cross", ".spv")
            : _crossOutputBasePath + ".spv";
        string directory = Path.GetDirectoryName(basePath) ?? Environment.CurrentDirectory;
        string fileName = Path.GetFileName(basePath);

        ImGuiFileBrowser.SaveFile("ShaderEditorSaveCrossSpirv", "Save SPIR-V", result =>
        {
            if (result.Success && !string.IsNullOrWhiteSpace(result.SelectedPath) && _crossSpirvBytes is { Length: > 0 })
                SaveBinaryFile(result.SelectedPath, _crossSpirvBytes);
        }, SpirvFileFilter, directory, fileName);
    }

    private void OpenCrossTextSaveDialog(string dialogId, string title, string extension, string filter, string content)
    {
        string basePath = string.IsNullOrWhiteSpace(_crossOutputBasePath)
            ? ShaderEditorServices.SuggestVariantPath(_shaderSourcePath, "cross", extension)
            : _crossOutputBasePath + extension;
        string directory = Path.GetDirectoryName(basePath) ?? Environment.CurrentDirectory;
        string fileName = Path.GetFileName(basePath);

        ImGuiFileBrowser.SaveFile(dialogId, title, result =>
        {
            if (result.Success && !string.IsNullOrWhiteSpace(result.SelectedPath))
                SaveTextFile(result.SelectedPath, content);
        }, filter, directory, fileName);
    }

    private static void SaveTextFile(string path, string content)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, content);
    }

    private static void SaveBinaryFile(string path, byte[] content)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllBytes(fullPath, content);
    }

    private void HandleSourceChanged(bool updateBoundSource = true)
    {
        _sourceRevision++;
        if (updateBoundSource)
            UpdateBoundSourceText(_shaderSource);
        _previewLine = Math.Clamp(_previewLine, 1, Math.Max(1, CountLines(_shaderSource)));
        _completionPrefix = ShaderEditorServices.GetTrailingIdentifierToken(_shaderSource);
        _instrumentedPreviewSource = string.Empty;
        _previewInstrumented = false;
        ResetGeneratedOutputs("Source changed.");
        _lockingToolRevision = -1;
        InvalidateIncludeCatalogs();
        EnsureCompletionItemsCurrent();
        SyncLockingToolFromCurrentSource();
        ScheduleCompile();
    }

    private void HandleSourceMetadataChanged()
    {
        _sourceRevision++;
        _lockingToolRevision = -1;
        ResetGeneratedOutputs("Shader type changed.");
        SyncLockingToolFromCurrentSource();
        ScheduleCompile();
    }

    private void SyncLockingToolFromCurrentSource()
    {
        if (_lockingToolRevision == _sourceRevision)
            return;

        try
        {
            _lockingTool.LoadShaderSource(_shaderSource, _shaderSourcePath, (EShaderType)_selectedShaderTypeIndex);
            _lockingToolRevision = _sourceRevision;
            _selectedVariantUniformIndex = -1;
        }
        catch (Exception ex)
        {
            _variantStatus = $"Optimizer unavailable: {ex.Message}";
        }
    }

    private void DrawAnnotatedSource(string source, IReadOnlyList<ShaderEditorDiagnostic> diagnostics)
    {
        IReadOnlyDictionary<int, List<ShaderEditorDiagnostic>> byLine = diagnostics
            .Where(diagnostic => diagnostic.Line > 0)
            .GroupBy(diagnostic => diagnostic.Line)
            .ToDictionary(group => group.Key, group => group.ToList());

        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        ImGui.BeginChild("ShaderEditorAnnotatedSource", Vector2.Zero, ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            int lineNumber = lineIndex + 1;
            bool hasDiagnostic = byLine.TryGetValue(lineNumber, out List<ShaderEditorDiagnostic>? lineDiagnostics);
            bool selected = _selectedDiagnosticLine == lineNumber;
            Vector2 cursor = ImGui.GetCursorScreenPos();

            if (selected)
            {
                uint selectedBg = ImGui.GetColorU32(new Vector4(0.25f, 0.45f, 0.85f, 0.22f));
                drawList.AddRectFilled(cursor, cursor + new Vector2(MathF.Max(600f, ImGui.GetContentRegionAvail().X), lineHeight), selectedBg);
                ImGui.SetScrollHereY(0.4f);
            }

            if (hasDiagnostic && lineDiagnostics is not null)
            {
                uint underline = ImGui.GetColorU32(GetDiagnosticColor(lineDiagnostics.MaxBy(diagnostic => diagnostic.Severity)!.Severity));
                drawList.AddLine(cursor + new Vector2(72f, lineHeight - 2f), cursor + new Vector2(MathF.Max(620f, ImGui.GetContentRegionAvail().X), lineHeight - 2f), underline, 1.5f);
            }

            ImGui.TextColored(hasDiagnostic ? GetDiagnosticColor(lineDiagnostics![0].Severity) : new Vector4(0.45f, 0.45f, 0.45f, 1f), lineNumber.ToString().PadLeft(4));
            ImGui.SameLine();
            ImGui.TextUnformatted(lines[lineIndex]);

            if (hasDiagnostic && ImGui.IsItemHovered() && lineDiagnostics is not null)
                ImGui.SetTooltip(string.Join("\n", lineDiagnostics.Select(diagnostic => diagnostic.Message)));
        }

        ImGui.EndChild();
    }

    private void DrawCompileStatusInline()
    {
        Vector4 color = _compileRunning
            ? new Vector4(0.45f, 0.7f, 1f, 1f)
            : _lastCompileResult?.Success == true
                ? new Vector4(0.45f, 0.85f, 0.45f, 1f)
                : _lastCompileResult is not null
                    ? new Vector4(0.95f, 0.45f, 0.45f, 1f)
                    : new Vector4(0.65f, 0.65f, 0.65f, 1f);

        ImGui.TextColored(color, _compileStatus);
    }

    private void OpenLoadDialog()
    {
        string initialDirectory = !string.IsNullOrWhiteSpace(_shaderSourcePath)
            ? Path.GetDirectoryName(_shaderSourcePath) ?? Environment.CurrentDirectory
            : Environment.CurrentDirectory;

        ImGuiFileBrowser.OpenFile("ShaderEditorOpenShader", "Open Shader", result =>
        {
            if (result.Success && !string.IsNullOrWhiteSpace(result.SelectedPath))
                LoadShaderFromPath(result.SelectedPath);
        }, ShaderFileFilter, initialDirectory);
    }

    private void OpenAbsoluteIncludeDialog()
    {
        string initialDirectory = !string.IsNullOrWhiteSpace(_absoluteIncludePath) && File.Exists(_absoluteIncludePath)
            ? Path.GetDirectoryName(Path.GetFullPath(_absoluteIncludePath)) ?? Environment.CurrentDirectory
            : !string.IsNullOrWhiteSpace(_shaderSourcePath)
                ? Path.GetDirectoryName(_shaderSourcePath) ?? Environment.CurrentDirectory
                : Environment.CurrentDirectory;

        ImGuiFileBrowser.OpenFile("ShaderEditorAbsoluteInclude", "Select Shader Include", result =>
        {
            if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedPath))
                return;

            string selectedPath = result.SelectedPath;
            ApplyUndoableChange("Select Absolute Shader Include", () =>
            {
                _absoluteIncludePath = Path.GetFullPath(selectedPath);
                _includeSourceKindIndex = (int)ShaderEditorIncludeSourceKind.AbsoluteInclude;
                RefreshIncludePreview();
            });
        }, ShaderFileFilter, initialDirectory);
    }

    private void OpenSaveDialog()
    {
        string initialDirectory = !string.IsNullOrWhiteSpace(_shaderSourcePath)
            ? Path.GetDirectoryName(_shaderSourcePath) ?? Environment.CurrentDirectory
            : Environment.CurrentDirectory;
        string initialName = !string.IsNullOrWhiteSpace(_shaderSourcePath)
            ? Path.GetFileName(_shaderSourcePath)
            : "Shader.frag";

        ImGuiFileBrowser.SaveFile("ShaderEditorSaveShader", "Save Shader", result =>
        {
            if (result.Success && !string.IsNullOrWhiteSpace(result.SelectedPath))
                SaveCurrentShaderAs(result.SelectedPath);
        }, ShaderFileFilter, initialDirectory, initialName);
    }

    private void SaveCurrentShader()
    {
        if (string.IsNullOrWhiteSpace(_shaderSourcePath))
        {
            OpenSaveDialog();
            return;
        }

        SaveCurrentShaderAs(_shaderSourcePath);
    }

    private void SaveCurrentShaderAs(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, _shaderSource);
            _shaderSourcePath = fullPath;
            if (_boundSource is not null)
                _boundSource.FilePath = fullPath;
            _compileStatus = "Saved. Compile pending.";
            _lockingToolRevision = -1;
            RefreshIncludeCatalogs(force: true);
            RefreshIncludePreview();
            ScheduleCompile();
        }
        catch (Exception ex)
        {
            EngineDebug.LogException(ex, $"Failed to save shader '{path}'.");
        }
    }

    private void SetShaderSourceText(string source, bool updateBoundSource = true)
    {
        source ??= string.Empty;
        if (string.Equals(_shaderSource, source, StringComparison.Ordinal) &&
            (!updateBoundSource || string.Equals(_boundSource?.Text ?? string.Empty, source, StringComparison.Ordinal)))
        {
            return;
        }

        _shaderSource = source;
        HandleSourceChanged(updateBoundSource);
    }

    private void BindSource(XRShader? shader, TextFile? source)
    {
        if (_boundSource is not null)
            _boundSource.TextChanged -= OnBoundSourceTextChanged;

        _boundShader = shader;
        _boundSource = source;

        if (_boundSource is not null)
        {
            Undo.Track(_boundSource);
            _boundSource.TextChanged += OnBoundSourceTextChanged;
        }

        if (_boundShader is not null)
            Undo.Track(_boundShader);
    }

    private void OnBoundSourceTextChanged()
    {
        if (_updatingBoundSource || _boundSource is null)
            return;

        string source = _boundSource.Text ?? string.Empty;
        if (string.Equals(source, _shaderSource, StringComparison.Ordinal))
            return;

        _shaderSource = source;
        HandleSourceChanged(updateBoundSource: false);
    }

    private void UpdateBoundSourceText(string source)
    {
        if (_boundSource is null)
            return;

        if (string.Equals(_boundSource.Text ?? string.Empty, source, StringComparison.Ordinal))
            return;

        try
        {
            _updatingBoundSource = true;
            _boundSource.Text = source;
        }
        finally
        {
            _updatingBoundSource = false;
        }
    }

    private void ResetGeneratedOutputs(string reason)
    {
        _variantSource = string.Empty;
        _variantStatus = reason.StartsWith("Source", StringComparison.OrdinalIgnoreCase)
            ? "Source changed; regenerate variant."
            : reason;
        _lastVariantResult = null;
        _analysisReport = null;
        _analysisStatus = reason;
        _crossSpirvBytes = null;
        _crossHlslSource = string.Empty;
        _crossGlslSource = string.Empty;
        _crossStatus = reason;
    }

    private void ApplyUndoableChange(string description, Action mutate, params XRBase?[] targets)
    {
        ShaderEditorSessionSnapshot before = CaptureSessionSnapshot();
        using IDisposable interaction = Undo.BeginUserInteraction();
        using Undo.ChangeScope scope = Undo.BeginChange(description);

        foreach (XRBase? target in targets)
            Undo.Track(target);

        mutate();
        AddSessionSnapshotStep(description, before, CaptureSessionSnapshot());
    }

    private void TrackSessionControlUndo(string description)
    {
        uint itemId = ImGui.GetItemID();
        if (itemId == 0)
            return;

        if (ImGui.IsItemActivated())
            _activeSessionUndoScopes[itemId] = CaptureSessionSnapshot();

        if (!ImGui.IsItemDeactivatedAfterEdit() && !ImGui.IsItemDeactivated())
            return;

        if (_activeSessionUndoScopes.Remove(itemId, out ShaderEditorSessionSnapshot before))
            RecordSessionSnapshotChange(description, before, CaptureSessionSnapshot());
    }

    private void RecordSessionSnapshotChange(string description, ShaderEditorSessionSnapshot before, ShaderEditorSessionSnapshot after)
    {
        if (before.Equals(after))
            return;

        using IDisposable interaction = Undo.BeginUserInteraction();
        using Undo.ChangeScope scope = Undo.BeginChange(description);
        AddSessionSnapshotStep(description, before, after);
    }

    private void AddSessionSnapshotStep(string description, ShaderEditorSessionSnapshot before, ShaderEditorSessionSnapshot after)
    {
        if (before.Equals(after))
            return;

        Undo.RecordStructuralChange(
            description,
            undoAction: () => ApplySessionSnapshot(before),
            redoAction: () => ApplySessionSnapshot(after));
    }

    private ShaderEditorSessionSnapshot CaptureSessionSnapshot()
        => new(
            _shaderSourcePath,
            _shaderSource,
            _resolvedSource,
            _selectedShaderTypeIndex,
            _entryPoint,
            _compileDebounceMs,
            _compileStatus,
            _selectedDiagnosticLine,
            _completionPrefix,
            _aiInstruction,
            _aiCompletion,
            _aiStatus,
            _previewLine,
            _previewExpression,
            _previewOutputVariable,
            _instrumentedPreviewSource,
            _previewInstrumented,
            _selectedVariantPresetIndex,
            _customVariantDefine,
            _variantSource,
            _variantStatus,
            _variantOutputPath,
            _compileVariantAfterGenerate,
            _variantUniformFilter,
            _variantNewAnimatedPattern,
            _variantShowOnlyAnimated,
            _variantShowOnlyLocked,
            _selectedVariantUniformIndex,
            _analysisSourceChoiceIndex,
            _analysisInvocationsPerFrame,
            _selectedResolutionPreset,
            _analysisFilter,
            _analysisSortByOccurrences,
            _analysisStatus,
            _crossSourceChoiceIndex,
            _crossSourceLanguage,
            _crossSpirvBytes is { Length: > 0 } ? Convert.ToBase64String(_crossSpirvBytes) : string.Empty,
            _crossHlslSource,
            _crossGlslSource,
            _crossStatus,
            _crossOutputBasePath,
            _includeSourceKindIndex,
            _selectedSnippetIndex,
            _selectedRelativeIncludeIndex,
            _absoluteIncludePath,
            _includePreviewText,
            _includePreviewStatus,
            _includePreviewDirective,
            _includePreviewResolvedPath,
            _lockingTool.OptimizeDeadCode,
            _lockingTool.EvaluateConstantExpressions,
            _lockingTool.RemoveUnusedUniforms,
            _lockingTool.InlineSingleUseConstants,
            JoinUndoTokens(_lockingTool.GetAnimatedPatterns()),
            JoinUndoTokens(_lockingTool.GetUniforms().Values.Where(static uniform => uniform.IsAnimated).Select(static uniform => uniform.Name)));

    private void ApplySessionSnapshot(ShaderEditorSessionSnapshot snapshot)
    {
        bool sourceChanged = !string.Equals(_shaderSource, snapshot.ShaderSource, StringComparison.Ordinal);
        bool typeChanged = _selectedShaderTypeIndex != snapshot.SelectedShaderTypeIndex;

        _shaderSourcePath = snapshot.ShaderSourcePath;
        _shaderSource = snapshot.ShaderSource;
        _resolvedSource = snapshot.ResolvedSource;
        _selectedShaderTypeIndex = snapshot.SelectedShaderTypeIndex;
        _entryPoint = snapshot.EntryPoint;
        _compileDebounceMs = snapshot.CompileDebounceMs;
        _compileStatus = snapshot.CompileStatus;
        _selectedDiagnosticLine = snapshot.SelectedDiagnosticLine;
        _completionPrefix = snapshot.CompletionPrefix;
        _aiInstruction = snapshot.AiInstruction;
        _aiCompletion = snapshot.AiCompletion;
        _aiStatus = snapshot.AiStatus;
        _previewLine = snapshot.PreviewLine;
        _previewExpression = snapshot.PreviewExpression;
        _previewOutputVariable = snapshot.PreviewOutputVariable;
        _instrumentedPreviewSource = snapshot.InstrumentedPreviewSource;
        _previewInstrumented = snapshot.PreviewInstrumented;
        _selectedVariantPresetIndex = snapshot.SelectedVariantPresetIndex;
        _customVariantDefine = snapshot.CustomVariantDefine;
        _variantSource = snapshot.VariantSource;
        _variantStatus = snapshot.VariantStatus;
        _variantOutputPath = snapshot.VariantOutputPath;
        _compileVariantAfterGenerate = snapshot.CompileVariantAfterGenerate;
        _variantUniformFilter = snapshot.VariantUniformFilter;
        _variantNewAnimatedPattern = snapshot.VariantNewAnimatedPattern;
        _variantShowOnlyAnimated = snapshot.VariantShowOnlyAnimated;
        _variantShowOnlyLocked = snapshot.VariantShowOnlyLocked;
        _selectedVariantUniformIndex = snapshot.SelectedVariantUniformIndex;
        _analysisSourceChoiceIndex = snapshot.AnalysisSourceChoiceIndex;
        _analysisInvocationsPerFrame = snapshot.AnalysisInvocationsPerFrame;
        _selectedResolutionPreset = snapshot.SelectedResolutionPreset;
        _analysisFilter = snapshot.AnalysisFilter;
        _analysisSortByOccurrences = snapshot.AnalysisSortByOccurrences;
        _analysisStatus = snapshot.AnalysisStatus;
        _crossSourceChoiceIndex = snapshot.CrossSourceChoiceIndex;
        _crossSourceLanguage = snapshot.CrossSourceLanguage;
        _crossSpirvBytes = string.IsNullOrEmpty(snapshot.CrossSpirvBase64) ? null : Convert.FromBase64String(snapshot.CrossSpirvBase64);
        _crossHlslSource = snapshot.CrossHlslSource;
        _crossGlslSource = snapshot.CrossGlslSource;
        _crossStatus = snapshot.CrossStatus;
        _crossOutputBasePath = snapshot.CrossOutputBasePath;
        _includeSourceKindIndex = snapshot.IncludeSourceKindIndex;
        _selectedSnippetIndex = snapshot.SelectedSnippetIndex;
        _selectedRelativeIncludeIndex = snapshot.SelectedRelativeIncludeIndex;
        _absoluteIncludePath = snapshot.AbsoluteIncludePath;
        _includePreviewText = snapshot.IncludePreviewText;
        _includePreviewStatus = snapshot.IncludePreviewStatus;
        _includePreviewDirective = snapshot.IncludePreviewDirective;
        _includePreviewResolvedPath = snapshot.IncludePreviewResolvedPath;

        if (_boundShader is not null)
            _boundShader.Type = (EShaderType)_selectedShaderTypeIndex;

        UpdateBoundSourceText(_shaderSource);

        if (sourceChanged || typeChanged)
        {
            _sourceRevision++;
            _lockingToolRevision = -1;
            EnsureCompletionItemsCurrent();
            SyncLockingToolFromCurrentSource();
            InvalidateIncludeCatalogs();
            ScheduleCompile();
        }

        ApplyLockingToolSnapshot(snapshot);
    }

    private void ApplyLockingToolSnapshot(ShaderEditorSessionSnapshot snapshot)
    {
        _lockingTool.OptimizeDeadCode = snapshot.LockingOptimizeDeadCode;
        _lockingTool.EvaluateConstantExpressions = snapshot.LockingEvaluateConstantExpressions;
        _lockingTool.RemoveUnusedUniforms = snapshot.LockingRemoveUnusedUniforms;
        _lockingTool.InlineSingleUseConstants = snapshot.LockingInlineSingleUseConstants;

        HashSet<string> desiredPatterns = SplitUndoTokens(snapshot.LockingAnimatedPatterns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string pattern in _lockingTool.GetAnimatedPatterns().ToArray())
        {
            if (!desiredPatterns.Contains(pattern))
                _lockingTool.RemoveAnimatedPattern(pattern);
        }

        foreach (string pattern in desiredPatterns)
            _lockingTool.AddAnimatedPattern(pattern);

        HashSet<string> animatedUniforms = SplitUndoTokens(snapshot.LockingAnimatedUniforms).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (UniformLockInfo uniform in _lockingTool.GetUniforms().Values.ToArray())
            _lockingTool.SetUniformAnimated(uniform.Name, animatedUniforms.Contains(uniform.Name));
    }

    private bool RefreshIncludeCatalogs(bool force)
    {
        if (!force && _includeListRevision == _sourceRevision)
            return false;

        _engineSnippetNames = ShaderEditorServices.GetEngineSnippetNames();
        _relativeIncludeCandidates = ShaderEditorServices.GetRelativeIncludeCandidates(_shaderSourcePath);
        _selectedSnippetIndex = ClampIndex(_selectedSnippetIndex, _engineSnippetNames.Count);
        _selectedRelativeIncludeIndex = ClampIndex(_selectedRelativeIncludeIndex, _relativeIncludeCandidates.Count);
        _includeListRevision = _sourceRevision;
        return true;
    }

    private void InvalidateIncludeCatalogs()
    {
        _includeListRevision = -1;
        _includePreviewText = string.Empty;
        _includePreviewStatus = "Select a snippet or include file.";
        _includePreviewDirective = string.Empty;
        _includePreviewResolvedPath = string.Empty;
    }

    private void RefreshIncludePreview()
    {
        ShaderEditorIncludeSourceKind kind = (ShaderEditorIncludeSourceKind)Math.Clamp(
            _includeSourceKindIndex,
            0,
            Enum.GetValues<ShaderEditorIncludeSourceKind>().Length - 1);

        ShaderEditorIncludePreview preview = kind switch
        {
            ShaderEditorIncludeSourceKind.EngineSnippet => _selectedSnippetIndex >= 0 && _selectedSnippetIndex < _engineSnippetNames.Count
                ? ShaderEditorServices.PreviewEngineSnippet(_engineSnippetNames[_selectedSnippetIndex])
                : new ShaderEditorIncludePreview(false, string.Empty, string.Empty, string.Empty, string.Empty, "Select an engine snippet."),
            ShaderEditorIncludeSourceKind.RelativeInclude => _selectedRelativeIncludeIndex >= 0 && _selectedRelativeIncludeIndex < _relativeIncludeCandidates.Count
                ? ShaderEditorServices.PreviewRelativeInclude(_relativeIncludeCandidates[_selectedRelativeIncludeIndex].IncludePath, _shaderSourcePath)
                : new ShaderEditorIncludePreview(false, string.Empty, string.Empty, string.Empty, string.Empty, "Select a relative include."),
            ShaderEditorIncludeSourceKind.AbsoluteInclude => ShaderEditorServices.PreviewAbsoluteInclude(_absoluteIncludePath),
            _ => new ShaderEditorIncludePreview(false, string.Empty, string.Empty, string.Empty, string.Empty, "Select a preview source.")
        };

        _includePreviewText = preview.Source;
        _includePreviewStatus = preview.Message;
        _includePreviewDirective = preview.Directive;
        _includePreviewResolvedPath = preview.ResolvedPath;
    }

    private static int ClampIndex(int index, int count)
        => count <= 0 ? -1 : Math.Clamp(index, 0, count - 1);

    private static string GetIncludeSourceKindLabel(ShaderEditorIncludeSourceKind kind)
        => kind switch
        {
            ShaderEditorIncludeSourceKind.EngineSnippet => "Engine Snippet",
            ShaderEditorIncludeSourceKind.RelativeInclude => "Relative Include",
            ShaderEditorIncludeSourceKind.AbsoluteInclude => "Absolute Include",
            _ => kind.ToString()
        };

    private static string JoinUndoTokens(IEnumerable<string> tokens)
        => string.Join('\u001F', tokens.Where(static token => !string.IsNullOrWhiteSpace(token)).OrderBy(static token => token, StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<string> SplitUndoTokens(string value)
        => string.IsNullOrEmpty(value)
            ? []
            : value.Split('\u001F', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void ScheduleCompile()
    {
        _compileDueTicks = Stopwatch.GetTimestamp() + MillisecondsToStopwatchTicks(_compileDebounceMs);
    }

    private void UpdateDebouncedCompile()
    {
        if (_compileDueTicks == 0)
            return;

        if (Stopwatch.GetTimestamp() < _compileDueTicks)
            return;

        StartCompileNow();
    }

    private void StartCompileNow(string? sourceOverride = null, string sourceLabel = "preview")
    {
        string source = sourceOverride ?? _shaderSource;
        if (string.IsNullOrWhiteSpace(source))
        {
            _compileStatus = "No source";
            return;
        }

        lock (_compileLock)
        {
            if (_compileRunning)
            {
                _compileDueTicks = Stopwatch.GetTimestamp() + MillisecondsToStopwatchTicks(150);
                return;
            }

            _compileDueTicks = 0;
            _compileRunning = true;
            _compileStatus = sourceOverride is null ? "Compiling..." : $"Compiling {sourceLabel}...";
        }

        int serial = Interlocked.Increment(ref _compileSerial);
        string path = _shaderSourcePath;
        EShaderType shaderType = (EShaderType)_selectedShaderTypeIndex;
        string entryPoint = string.IsNullOrWhiteSpace(_entryPoint) ? "main" : _entryPoint.Trim();

        _ = Task.Run(() => ShaderEditorServices.CompileGlsl(source, path, shaderType, entryPoint))
            .ContinueWith(task =>
            {
                ShaderEditorCompileResult result;
                if (task.Exception is not null)
                {
                    string message = task.Exception.GetBaseException().Message;
                    result = new ShaderEditorCompileResult(
                        false,
                        source,
                        source,
                        [new ShaderEditorDiagnostic(0, 0, ShaderEditorDiagnosticSeverity.Error, message, message)],
                        TimeSpan.Zero,
                        DateTimeOffset.Now,
                        0,
                        false);
                }
                else
                {
                    result = task.Result;
                }

                if (serial != Volatile.Read(ref _compileSerial))
                    return;

                _lastCompileResult = result;
                _resolvedSource = result.ResolvedSource;
                _diagnostics = result.Diagnostics;
                _compileStatus = result.Success
                    ? $"OK ({result.Duration.TotalMilliseconds:F0} ms)"
                    : $"{result.Diagnostics.Count(diagnostic => diagnostic.Severity == ShaderEditorDiagnosticSeverity.Error)} error(s)";

                lock (_compileLock)
                    _compileRunning = false;

                if (sourceOverride is null && !string.Equals(source, _shaderSource, StringComparison.Ordinal))
                    ScheduleCompile();
            }, TaskScheduler.Default);
    }

    private void EnsureCompletionItemsCurrent()
    {
        _completionItems = ShaderEditorServices.BuildCompletionItems(_shaderSource);
    }

    private void RequestAiCompletion()
    {
        string apiKey = ResolveOpenAiApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _aiStatus = "OpenAI key missing";
            return;
        }

        lock (_aiLock)
        {
            if (_aiRunning)
                return;

            _aiRunning = true;
            _aiStatus = "Requesting...";
        }

        int serial = Interlocked.Increment(ref _aiSerial);
        string model = Engine.EditorPreferences?.McpAssistantOpenAiModel ?? "gpt-5-codex";
        string source = _shaderSource;
        string instruction = _aiInstruction;
        string diagnostics = string.Join("\n", _diagnostics.Take(8).Select(diagnostic => $"{diagnostic.Severity} line {diagnostic.Line}: {diagnostic.Message}"));

        _ = RequestOpenAiCompletionAsync(apiKey, model, source, instruction, diagnostics)
            .ContinueWith(task =>
            {
                if (serial != Volatile.Read(ref _aiSerial))
                    return;

                if (task.Exception is not null)
                {
                    _aiStatus = task.Exception.GetBaseException().Message;
                }
                else
                {
                    _aiCompletion = task.Result.Trim();
                    _aiStatus = string.IsNullOrWhiteSpace(_aiCompletion) ? "No completion" : "Completion ready";
                }

                lock (_aiLock)
                    _aiRunning = false;
            }, TaskScheduler.Default);
    }

    private static async Task<string> RequestOpenAiCompletionAsync(
        string apiKey,
        string model,
        string source,
        string instruction,
        string diagnostics)
    {
        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? "gpt-5-codex" : model.Trim(),
            ["instructions"] = "You are completing GLSL shader code inside XRENGINE. Return only GLSL code, no markdown fences.",
            ["input"] = $"{instruction}\n\nDiagnostics:\n{diagnostics}\n\nShader source:\n{source}",
            ["max_output_tokens"] = 768
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using HttpResponseMessage response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI HTTP {(int)response.StatusCode}: {body}");

        return ShaderEditorServices.ExtractOpenAiResponseText(body);
    }

    private void AppendAiCompletion()
    {
        if (string.IsNullOrWhiteSpace(_aiCompletion))
            return;

        string source = _shaderSource;
        if (source.Length > 0 && !source.EndsWith('\n'))
            source += Environment.NewLine;

        source += _aiCompletion;
        _aiCompletion = string.Empty;
        SetShaderSourceText(source);
    }

    private static string ResolveOpenAiApiKey()
    {
        string? preferenceKey = Engine.EditorPreferences?.McpAssistantOpenAiApiKey;
        if (!string.IsNullOrWhiteSpace(preferenceKey))
            return preferenceKey;

        return Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
    }

    private static Vector4 GetDiagnosticColor(ShaderEditorDiagnosticSeverity severity)
        => severity switch
        {
            ShaderEditorDiagnosticSeverity.Error => new Vector4(0.95f, 0.35f, 0.35f, 1f),
            ShaderEditorDiagnosticSeverity.Warning => new Vector4(0.95f, 0.70f, 0.25f, 1f),
            _ => new Vector4(0.55f, 0.75f, 1f, 1f)
        };

    private static long MillisecondsToStopwatchTicks(int milliseconds)
        => (long)(Math.Max(0, milliseconds) * Stopwatch.Frequency / 1000.0);

    private static int CountLines(string source)
        => string.IsNullOrEmpty(source) ? 1 : source.Count(character => character == '\n') + 1;

    private static string GetSourceMetrics(string source)
        => $"{CountLines(source)} lines, {source.Length:N0} chars";

    private static string GetDefaultFragmentShader()
        => """
           #version 460 core

           out vec4 FragColor;

           void main()
           {
               vec3 color = vec3(0.25, 0.55, 1.0);
               FragColor = vec4(color, 1.0);
           }
           """;

    private readonly record struct ShaderEditorSessionSnapshot(
        string ShaderSourcePath,
        string ShaderSource,
        string ResolvedSource,
        int SelectedShaderTypeIndex,
        string EntryPoint,
        int CompileDebounceMs,
        string CompileStatus,
        int SelectedDiagnosticLine,
        string CompletionPrefix,
        string AiInstruction,
        string AiCompletion,
        string AiStatus,
        int PreviewLine,
        string PreviewExpression,
        string PreviewOutputVariable,
        string InstrumentedPreviewSource,
        bool PreviewInstrumented,
        int SelectedVariantPresetIndex,
        string CustomVariantDefine,
        string VariantSource,
        string VariantStatus,
        string VariantOutputPath,
        bool CompileVariantAfterGenerate,
        string VariantUniformFilter,
        string VariantNewAnimatedPattern,
        bool VariantShowOnlyAnimated,
        bool VariantShowOnlyLocked,
        int SelectedVariantUniformIndex,
        int AnalysisSourceChoiceIndex,
        int AnalysisInvocationsPerFrame,
        int SelectedResolutionPreset,
        string AnalysisFilter,
        bool AnalysisSortByOccurrences,
        string AnalysisStatus,
        int CrossSourceChoiceIndex,
        ShaderSourceLanguage CrossSourceLanguage,
        string CrossSpirvBase64,
        string CrossHlslSource,
        string CrossGlslSource,
        string CrossStatus,
        string CrossOutputBasePath,
        int IncludeSourceKindIndex,
        int SelectedSnippetIndex,
        int SelectedRelativeIncludeIndex,
        string AbsoluteIncludePath,
        string IncludePreviewText,
        string IncludePreviewStatus,
        string IncludePreviewDirective,
        string IncludePreviewResolvedPath,
        bool LockingOptimizeDeadCode,
        bool LockingEvaluateConstantExpressions,
        bool LockingRemoveUnusedUniforms,
        bool LockingInlineSingleUseConstants,
        string LockingAnimatedPatterns,
        string LockingAnimatedUniforms);

    private readonly struct DisabledScope : IDisposable
    {
        private readonly bool _disabled;

        public DisabledScope(bool disabled)
        {
            _disabled = disabled;
            if (disabled)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (_disabled)
                ImGui.EndDisabled();
        }
    }
}
