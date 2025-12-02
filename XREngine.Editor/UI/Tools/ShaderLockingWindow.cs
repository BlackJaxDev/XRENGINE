using ImGuiNET;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// ImGui-based window for the Shader Locking Tool.
/// Accessible from Tools menu.
/// </summary>
public class ShaderLockingWindow
{
    private static ShaderLockingWindow? _instance;
    public static ShaderLockingWindow Instance => _instance ??= new ShaderLockingWindow();

    private ShaderLockingTool _tool = new();
    private bool _isOpen = false;
    private string _shaderSourcePath = "";
    private string _outputPath = "";
    private string _newPattern = "";
    private int _selectedUniformIndex = -1;
    private Vector2 _previewScrollPos = Vector2.Zero;

    // Filter for uniform list
    private string _uniformFilter = "";
    private bool _showOnlyAnimated = false;
    private bool _showOnlyLocked = false;

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

        ImGui.SetNextWindowSize(new Vector2(900, 700), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Shader Locking Tool", ref _isOpen, ImGuiWindowFlags.MenuBar))
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
                    LoadShaderDialog();

                if (ImGui.MenuItem("Load Material..."))
                    LoadMaterialDialog();

                ImGui.Separator();

                if (ImGui.MenuItem("Export Locked Shader...", null, false, !string.IsNullOrEmpty(_tool.GetPreviewText())))
                    ExportShaderDialog();

                if (ImGui.MenuItem("Export All Material Shaders...", null, false, _tool.SelectedMaterial != null))
                    ExportAllShadersDialog();

                ImGui.Separator();

                if (ImGui.MenuItem("Close"))
                    _isOpen = false;

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Options"))
            {
                bool opt = _tool.OptimizeDeadCode;
                if (ImGui.MenuItem("Optimize Dead Code", null, ref opt))
                    _tool.OptimizeDeadCode = opt;

                opt = _tool.EvaluateConstantExpressions;
                if (ImGui.MenuItem("Evaluate Constant Expressions", null, ref opt))
                    _tool.EvaluateConstantExpressions = opt;

                opt = _tool.RemoveUnusedUniforms;
                if (ImGui.MenuItem("Remove Unused Uniforms", null, ref opt))
                    _tool.RemoveUnusedUniforms = opt;

                opt = _tool.InlineSingleUseConstants;
                if (ImGui.MenuItem("Inline Single-Use Constants", null, ref opt))
                    _tool.InlineSingleUseConstants = opt;

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
        //    ImGui.Text("Shader Locking Tool");
        //    ImGui.Separator();
        //    ImGui.TextWrapped("This tool generates optimized 'locked' versions of shaders by:\n" +
        //                    "- Replacing non-animated uniforms with constant values\n" +
        //                    "- Evaluating compile-time conditionals\n" +
        //                    "- Removing dead code branches\n" +
        //                    "- Inlining single-use constants");
        //    ImGui.EndPopup();
        //}
    }

    private void DrawMainContent()
    {
        // Split into left panel (uniforms) and right panel (preview)
        float leftWidth = 350;

        ImGui.BeginChild("LeftPanel", new Vector2(leftWidth, 0), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX);
        DrawUniformsPanel();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("RightPanel", Vector2.Zero, ImGuiChildFlags.Border);
        DrawPreviewPanel();
        ImGui.EndChild();
    }

    private void DrawUniformsPanel()
    {
        ImGui.Text("Uniforms");
        ImGui.Separator();

        // Animated patterns section
        if (ImGui.CollapsingHeader("Animated Patterns", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Uniforms matching these patterns will remain as runtime uniforms (not locked):");

            ImGui.BeginChild("PatternsList", new Vector2(0, 100), ImGuiChildFlags.Border);
            var patterns = GetAnimatedPatterns();
            for (int i = 0; i < patterns.Count; i++)
            {
                ImGui.PushID(i);
                ImGui.Text(patterns[i]);
                ImGui.SameLine();
                if (ImGui.SmallButton("X"))
                {
                    _tool.RemoveAnimatedPattern(patterns[i]);
                }
                ImGui.PopID();
            }
            ImGui.EndChild();

            ImGui.SetNextItemWidth(-60);
            ImGui.InputText("##NewPattern", ref _newPattern, 256);
            ImGui.SameLine();
            if (ImGui.Button("Add") && !string.IsNullOrWhiteSpace(_newPattern))
            {
                _tool.AddAnimatedPattern(_newPattern);
                _newPattern = "";
            }
        }

        ImGui.Separator();

        // Filter
        ImGui.Text("Filter:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##UniformFilter", ref _uniformFilter, 256);

        ImGui.Checkbox("Show Animated Only", ref _showOnlyAnimated);
        ImGui.SameLine();
        ImGui.Checkbox("Show Locked Only", ref _showOnlyLocked);

        ImGui.Separator();

        // Uniform list
        ImGui.BeginChild("UniformList", Vector2.Zero, ImGuiChildFlags.None);

        var uniforms = _tool.GetUniforms();
        int index = 0;

        foreach (var kvp in uniforms)
        {
            var info = kvp.Value;

            // Apply filters
            if (!string.IsNullOrEmpty(_uniformFilter) &&
                !info.Name.Contains(_uniformFilter, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (_showOnlyAnimated && !info.IsAnimated)
            {
                index++;
                continue;
            }

            if (_showOnlyLocked && info.IsAnimated)
            {
                index++;
                continue;
            }

            ImGui.PushID(index);

            // Color based on animated status
            Vector4 color = info.IsAnimated
                ? new Vector4(0.3f, 0.8f, 0.3f, 1.0f)  // Green for animated
                : new Vector4(0.8f, 0.5f, 0.2f, 1.0f); // Orange for locked

            ImGui.PushStyleColor(ImGuiCol.Text, color);

            bool isSelected = _selectedUniformIndex == index;
            if (ImGui.Selectable($"{info.Name}", isSelected))
            {
                _selectedUniformIndex = index;
            }

            ImGui.PopStyleColor();

            // Tooltip
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text($"Name: {info.Name}");
                ImGui.Text($"Type: {info.TypeString ?? info.Type.ToString()}");
                ImGui.Text($"Value: {info.Value}");
                ImGui.Text($"Status: {(info.IsAnimated ? "Animated (kept as uniform)" : "Locked (will be constant)")}");
                ImGui.EndTooltip();
            }

            // Context menu
            if (ImGui.BeginPopupContextItem())
            {
                if (ImGui.MenuItem(info.IsAnimated ? "Lock (Convert to Constant)" : "Animate (Keep as Uniform)"))
                {
                    _tool.SetUniformAnimated(info.Name, !info.IsAnimated);
                }
                ImGui.EndPopup();
            }

            ImGui.PopID();
            index++;
        }

        ImGui.EndChild();
    }

    private void DrawPreviewPanel()
    {
        ImGui.Text("Preview");
        ImGui.Separator();

        // Shader info
        if (_tool.SelectedShader != null)
        {
            ImGui.Text($"Shader: {_tool.SelectedShader.Source?.FilePath ?? "Unnamed"}");
            ImGui.Text($"Type: {_tool.SelectedShader.Type}");
        }
        else
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "No shader loaded. Use File > Load Shader or Load Material.");
        }

        ImGui.Separator();

        // Preview text
        string preview = _tool.GetPreviewText();
        if (!string.IsNullOrEmpty(preview))
        {
            // Line count
            int lineCount = preview.Count(c => c == '\n') + 1;
            ImGui.Text($"Lines: {lineCount}");

            // Statistics
            var uniforms = _tool.GetUniforms();
            int lockedCount = uniforms.Count(u => !u.Value.IsAnimated);
            int animatedCount = uniforms.Count(u => u.Value.IsAnimated);
            ImGui.Text($"Uniforms: {lockedCount} locked, {animatedCount} animated");

            ImGui.Separator();

            // Scrollable preview with syntax highlighting (basic)
            ImGui.BeginChild("PreviewScroll", Vector2.Zero, ImGuiChildFlags.Border);

            // Use InputTextMultiline for the preview (read-only)
            Vector2 size = new(-1, -1);
            ImGui.InputTextMultiline("##Preview", ref preview, (uint)preview.Length + 1, size,
                ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);

            ImGui.EndChild();
        }
        else
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No preview available.");
        }
    }

    #region Dialog Methods

    private void LoadShaderDialog()
    {
        // In a real implementation, you'd use a file dialog
        // For now, we'll use a simple input field popup
        ImGui.OpenPopup("LoadShaderPopup");
    }

    private void LoadMaterialDialog()
    {
        ImGui.OpenPopup("LoadMaterialPopup");
    }

    private void ExportShaderDialog()
    {
        ImGui.OpenPopup("ExportShaderPopup");
    }

    private void ExportAllShadersDialog()
    {
        ImGui.OpenPopup("ExportAllShadersPopup");
    }

    /// <summary>
    /// Call this in Render() to handle popup dialogs.
    /// </summary>
    public void RenderDialogs()
    {
        // Load Shader popup
        if (ImGui.BeginPopupModal("LoadShaderPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Enter shader file path:");
            ImGui.SetNextItemWidth(400);
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

        // Export Shader popup
        if (ImGui.BeginPopupModal("ExportShaderPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Enter output file path:");
            ImGui.SetNextItemWidth(400);
            ImGui.InputText("##OutputPath", ref _outputPath, 1024);

            if (ImGui.Button("Export") && !string.IsNullOrEmpty(_outputPath))
            {
                if (_tool.ExportLockedShader(_outputPath))
                {
                    Debug.Out($"Exported locked shader to: {_outputPath}");
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        // Export All Shaders popup
        if (ImGui.BeginPopupModal("ExportAllShadersPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Enter output directory:");
            ImGui.SetNextItemWidth(400);
            ImGui.InputText("##OutputDir", ref _outputPath, 1024);

            ImGui.Text("Output suffix:");
            string suffix = _tool.OutputSuffix;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputText("##Suffix", ref suffix, 64))
                _tool.OutputSuffix = suffix;

            if (ImGui.Button("Export All") && !string.IsNullOrEmpty(_outputPath))
            {
                _tool.ExportAllShadersFromMaterial(_outputPath);
                Debug.Out($"Exported all locked shaders to: {_outputPath}");
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
    }

    #endregion

    #region Shader Loading

    public void LoadShaderFromPath(string path)
    {
        if (!File.Exists(path))
        {
            Debug.LogError($"Shader file not found: {path}");
            return;
        }

        try
        {
            string source = File.ReadAllText(path);
            var shader = new XRShader(XRShader.ResolveType(Path.GetExtension(path)))
            {
                Source = new Core.Files.TextFile { Text = source, FilePath = path }
            };

            _tool.SelectedShader = shader;
            _shaderSourcePath = path;

            // Generate default output path
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            _outputPath = Path.Combine(dir, $"{name}_locked{ext}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to load shader: {ex.Message}");
        }
    }

    public void LoadMaterial(XRMaterial material)
    {
        _tool.SelectedMaterial = material;
    }

    #endregion

    #region Helpers

    private List<string> GetAnimatedPatterns()
    {
        // Access the patterns from the tool
        return [.. _tool.GetAnimatedPatterns()];
    }

    #endregion
}
