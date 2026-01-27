using ImGuiNET;
using System;
using System.IO;
using System.Numerics;
using XREngine.Diagnostics;
using XREngine.Rendering;
using SPIRVCross;
using System.Text;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// ImGui-based window for cross compiling GLSL shaders to SPIR-V and HLSL.
/// </summary>
public sealed class ShaderCrossCompilerWindow
{
    private static ShaderCrossCompilerWindow? _instance;
    public static ShaderCrossCompilerWindow Instance => _instance ??= new ShaderCrossCompilerWindow();

    private bool _isOpen;
    private string _shaderSourcePath = string.Empty;
    private string _shaderSource = string.Empty;
    private string _hlslSource = string.Empty;
    private string _glslOutput = string.Empty;
    private byte[]? _spirvBytes;
    private string _entryPoint = "main";
    private int _selectedShaderTypeIndex = (int)EShaderType.Fragment;
    private string _errorMessage = string.Empty;
    private string _outputPath = string.Empty;
    private ShaderSourceLanguage _sourceLanguage = ShaderSourceLanguage.Glsl;

    public bool IsOpen
    {
        get => _isOpen;
        set => _isOpen = value;
    }

    public void Open() => _isOpen = true;
    public void Close() => _isOpen = false;
    public void Toggle() => _isOpen = !_isOpen;

    public void LoadShaderFromPath(string path)
    {
        if (!File.Exists(path))
        {
            Debug.LogError($"Shader file not found: {path}");
            return;
        }

        _shaderSourcePath = path;
        _shaderSource = File.ReadAllText(path);
        _selectedShaderTypeIndex = (int)XRShader.ResolveType(Path.GetExtension(path));
        _sourceLanguage = ShaderSourceLanguage.Glsl;
        _errorMessage = string.Empty;
        _spirvBytes = null;
        _hlslSource = string.Empty;
        _glslOutput = string.Empty;
    }

    public void Render()
    {
        if (!_isOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(1100, 750), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Shader Cross-Compiler", ref _isOpen, ImGuiWindowFlags.MenuBar))
        {
            DrawMenuBar();
            DrawMainContent();
        }

        ImGui.End();
    }

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMenuBar())
            return;

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Load Shader..."))
                ImGui.OpenPopup("LoadShaderPopup");

            if (ImGui.MenuItem("Paste from Clipboard"))
                PasteFromClipboard();

            ImGui.Separator();

            bool hasSpirv = _spirvBytes is { Length: > 0 };
            bool hasHlsl = !string.IsNullOrWhiteSpace(_hlslSource);
            bool hasGlsl = !string.IsNullOrWhiteSpace(_glslOutput);

            if (ImGui.MenuItem("Export SPIR-V...", null, false, hasSpirv))
                ImGui.OpenPopup("ExportSpirvPopup");

            if (ImGui.MenuItem("Export HLSL...", null, false, hasHlsl))
                ImGui.OpenPopup("ExportHlslPopup");

            if (ImGui.MenuItem("Export GLSL...", null, false, hasGlsl))
                ImGui.OpenPopup("ExportGlslPopup");

            if (ImGui.MenuItem("Export Both...", null, false, hasSpirv && hasHlsl))
                ImGui.OpenPopup("ExportBothPopup");

            if (ImGui.MenuItem("Export All...", null, false, hasSpirv && (hasHlsl || hasGlsl)))
                ImGui.OpenPopup("ExportAllPopup");

            ImGui.Separator();

            if (ImGui.MenuItem("Close"))
                _isOpen = false;

            ImGui.EndMenu();
        }

        ImGui.EndMenuBar();

        DrawLoadShaderPopup();
        DrawExportPopups();
    }

    private void DrawMainContent()
    {
        DrawSettingsPanel();

        if (!string.IsNullOrWhiteSpace(_errorMessage))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _errorMessage);
            ImGui.Separator();
        }

        float availableHeight = ImGui.GetContentRegionAvail().Y;

        ImGui.BeginChild("SourcePanel", new Vector2(480, availableHeight), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX);
        DrawSourcePanel();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("OutputPanel", Vector2.Zero, ImGuiChildFlags.Border);
        DrawOutputPanel();
        ImGui.EndChild();
    }

    private void DrawSettingsPanel()
    {
        ImGui.Text("Shader Type:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);

        string preview = ((EShaderType)_selectedShaderTypeIndex).ToString();
        if (ImGui.BeginCombo("##ShaderType", preview))
        {
            foreach (EShaderType value in Enum.GetValues<EShaderType>())
            {
                bool isSelected = _selectedShaderTypeIndex == (int)value;
                if (ImGui.Selectable(value.ToString(), isSelected))
                    _selectedShaderTypeIndex = (int)value;

                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.Text("Source Language:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("##SourceLanguage", _sourceLanguage.ToString()))
        {
            foreach (ShaderSourceLanguage value in Enum.GetValues<ShaderSourceLanguage>())
            {
                bool isSelected = _sourceLanguage == value;
                if (ImGui.Selectable(value.ToString(), isSelected))
                    _sourceLanguage = value;

                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.Text("Entry Point:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("##EntryPoint", ref _entryPoint, 128);

        if (ImGui.Button("Compile SPIR-V"))
            CompileSpirv();

        ImGui.SameLine();
        if (ImGui.Button("Compile HLSL"))
            CompileHlsl();

        ImGui.SameLine();
        if (ImGui.Button("Compile GLSL"))
            CompileGlsl();

        ImGui.SameLine();
        if (ImGui.Button("Compile All"))
        {
            if (CompileSpirv())
            {
                CompileHlsl(skipSpirvCompile: true);
                CompileGlsl(skipSpirvCompile: true);
            }
        }
    }

    private void DrawSourcePanel()
    {
        ImGui.Text("GLSL Source");
        ImGui.Separator();
        ImGui.InputTextMultiline("##ShaderSource", ref _shaderSource, 1024 * 1024, new Vector2(-1, -1));
    }

    private void DrawOutputPanel()
    {
        ImGui.Text("Output");
        ImGui.Separator();

        if (_spirvBytes is { Length: > 0 })
            ImGui.Text($"SPIR-V Size: {_spirvBytes.Length} bytes");
        else
            ImGui.Text("SPIR-V not generated yet.");

        ImGui.Separator();
        ImGui.Text("HLSL Output");
        ImGui.Separator();
        ImGui.InputTextMultiline("##HlslOutput", ref _hlslSource, 1024 * 1024, new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);

        ImGui.Separator();
        ImGui.Text("GLSL Output");
        ImGui.Separator();
        ImGui.InputTextMultiline("##GlslOutput", ref _glslOutput, 1024 * 1024, new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
    }

    private void DrawLoadShaderPopup()
    {
        if (ImGui.BeginPopupModal("LoadShaderPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.InputText("##ShaderPath", ref _shaderSourcePath, 1024);
            if (ImGui.Button("Load"))
            {
                LoadShaderFromPath(_shaderSourcePath);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
    }

    private void DrawExportPopups()
    {
        if (ImGui.BeginPopupModal("ExportSpirvPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            DrawExportPopup("SPIR-V Output Path", ".spv", () => SaveSpirv(_outputPath));
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("ExportHlslPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            DrawExportPopup("HLSL Output Path", ".hlsl", () => SaveHlsl(_outputPath));
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("ExportGlslPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            DrawExportPopup("GLSL Output Path", ".glsl", () => SaveGlsl(_outputPath));
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("ExportBothPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Base Output Path (without extension):");
            ImGui.InputText("##ExportBothPath", ref _outputPath, 1024);

            if (ImGui.Button("Export"))
            {
                SaveSpirv($"{_outputPath}.spv");
                SaveHlsl($"{_outputPath}.hlsl");
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("ExportAllPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Base Output Path (without extension):");
            ImGui.InputText("##ExportAllPath", ref _outputPath, 1024);

            if (ImGui.Button("Export"))
            {
                SaveSpirv($"{_outputPath}.spv");
                if (!string.IsNullOrWhiteSpace(_hlslSource))
                    SaveHlsl($"{_outputPath}.hlsl");
                if (!string.IsNullOrWhiteSpace(_glslOutput))
                    SaveGlsl($"{_outputPath}.glsl");
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
    }

    private void DrawExportPopup(string label, string extension, Action onExport)
    {
        ImGui.Text(label);
        ImGui.InputText("##ExportPath", ref _outputPath, 1024);

        if (ImGui.Button("Export"))
        {
            if (!string.IsNullOrWhiteSpace(_outputPath) && !Path.HasExtension(_outputPath))
                _outputPath += extension;

            onExport();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
    }

    private void PasteFromClipboard()
    {
        _shaderSource = ImGui.GetClipboardText() ?? string.Empty;
        _shaderSourcePath = string.Empty;
        _errorMessage = string.Empty;
        _spirvBytes = null;
        _hlslSource = string.Empty;
        _glslOutput = string.Empty;
    }

    private bool CompileSpirv()
    {
        try
        {
            _spirvBytes = ShaderCrossCompiler.CompileToSpirv(
                _shaderSource,
                (EShaderType)_selectedShaderTypeIndex,
                _sourceLanguage,
                Path.GetFileName(_shaderSourcePath),
                _entryPoint);
            _errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _spirvBytes = null;
            _errorMessage = ex.Message;
            return false;
        }
    }

    private void CompileHlsl(bool skipSpirvCompile = false)
    {
        if (!skipSpirvCompile && !CompileSpirv())
            return;

        if (_spirvBytes is null || _spirvBytes.Length == 0)
        {
            _errorMessage = "SPIR-V compilation failed; HLSL generation skipped.";
            return;
        }

        try
        {
            _hlslSource = CrossCompileSpirv(_spirvBytes, spvc_backend.Hlsl, ConfigureHlslOptions);
            _errorMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _hlslSource = string.Empty;
            _errorMessage = ex.Message;
        }
    }

    private void CompileGlsl(bool skipSpirvCompile = false)
    {
        if (!skipSpirvCompile && !CompileSpirv())
            return;

        if (_spirvBytes is null || _spirvBytes.Length == 0)
        {
            _errorMessage = "SPIR-V compilation failed; GLSL generation skipped.";
            return;
        }

        try
        {
            _glslOutput = CrossCompileSpirv(_spirvBytes, spvc_backend.Glsl, ConfigureGlslOptions);
            _errorMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _glslOutput = string.Empty;
            _errorMessage = ex.Message;
        }
    }

    private static unsafe string CrossCompileSpirv(byte[] spirvBytes, spvc_backend backend, Action<spvc_compiler_options>? configureOptions)
    {
        if (spirvBytes.Length % 4 != 0)
            throw new InvalidOperationException("SPIR-V bytecode length is not aligned.");

        spvc_context context;
        EnsureSuccess(SPIRV.spvc_context_create(&context), context, "Failed to create SPIRV-Cross context.");

        try
        {
            spvc_parsed_ir parsedIr;
            fixed (byte* bytes = spirvBytes)
            {
                var words = (SpvId*)bytes;
                uint wordCount = (uint)(spirvBytes.Length / 4);
                EnsureSuccess(SPIRV.spvc_context_parse_spirv(context, words, wordCount, &parsedIr), context, "Failed to parse SPIR-V module.");
            }

            spvc_compiler compiler;
            EnsureSuccess(SPIRV.spvc_context_create_compiler(context, backend, parsedIr, spvc_capture_mode.TakeOwnership, &compiler), context,
                "Failed to create SPIRV-Cross compiler.");

            spvc_compiler_options options;
            EnsureSuccess(SPIRV.spvc_compiler_create_compiler_options(compiler, &options), context, "Failed to create SPIRV-Cross compiler options.");
            configureOptions?.Invoke(options);
            EnsureSuccess(SPIRV.spvc_compiler_install_compiler_options(compiler, options), context, "Failed to install SPIRV-Cross options.");

            byte* sourcePtr = null;
            EnsureSuccess(SPIRV.spvc_compiler_compile(compiler, (byte*)&sourcePtr), context, "SPIRV-Cross compilation failed.");

            if (sourcePtr == null)
                throw new InvalidOperationException("SPIRV-Cross produced empty output.");

            return GetString(sourcePtr);
        }
        finally
        {
            SPIRV.spvc_context_destroy(context);
        }
    }

    private static void ConfigureHlslOptions(spvc_compiler_options options)
    {
        SPIRV.spvc_compiler_options_set_uint(options, spvc_compiler_option.HlslShaderModel, 50);
        SPIRV.spvc_compiler_options_set_bool(options, spvc_compiler_option.HlslPointSizeCompat, true);
    }

    private static void ConfigureGlslOptions(spvc_compiler_options options)
    {
        SPIRV.spvc_compiler_options_set_uint(options, spvc_compiler_option.GlslVersion, 450);
        SPIRV.spvc_compiler_options_set_bool(options, spvc_compiler_option.GlslVulkanSemantics, true);
    }

    private static unsafe void EnsureSuccess(spvc_result result, spvc_context context, string message)
    {
        if (result == spvc_result.SPVC_SUCCESS)
            return;

        string detail = GetString(SPIRV.spvc_context_get_last_error_string(context));
        throw new InvalidOperationException($"{message} {detail}".Trim());
    }

    private static unsafe string GetString(byte* ptr)
    {
        if (ptr == null)
            return string.Empty;

        int length = 0;
        while (length < 1_000_000 && ptr[length] != 0)
            length++;

        return Encoding.UTF8.GetString(ptr, length);
    }

    private void SaveSpirv(string path)
    {
        if (_spirvBytes is null || _spirvBytes.Length == 0)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, _spirvBytes);
    }

    private void SaveHlsl(string path)
    {
        if (string.IsNullOrWhiteSpace(_hlslSource))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, _hlslSource);
    }

    private void SaveGlsl(string path)
    {
        if (string.IsNullOrWhiteSpace(_glslOutput))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, _glslOutput);
    }
}
