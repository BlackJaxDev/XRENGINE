using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI.Tools;

/// <summary>
/// A tool panel for generating "locked" versions of shaders.
/// Replaces non-animated uniforms with constant values and optimizes code paths.
/// </summary>
public partial class ShaderLockingTool : EditorPanel
{
    #region Fields

    private XRMaterial? _selectedMaterial;
    private XRShader? _selectedShader;
    private string _outputDirectory = "";
    private string _outputSuffix = "_locked";
    private bool _showPreview = true;
    private string _previewText = "";
    private Dictionary<string, UniformLockInfo> _uniforms = [];
    private List<string> _animatedUniformPatterns = ["*Time*", "*Animation*", "*Scroll*", "*Pan*"];
    private bool _optimizeDeadCode = true;
    private bool _evaluateConstantExpressions = true;
    private bool _removeUnusedUniforms = true;
    private bool _inlineSingleUseConstants = true;

    #endregion

    #region Properties

    public XRMaterial? SelectedMaterial
    {
        get => _selectedMaterial;
        set
        {
            if (SetField(ref _selectedMaterial, value))
                OnMaterialChanged();
        }
    }

    public XRShader? SelectedShader
    {
        get => _selectedShader;
        set
        {
            if (SetField(ref _selectedShader, value))
                OnShaderChanged();
        }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetField(ref _outputDirectory, value);
    }

    public string OutputSuffix
    {
        get => _outputSuffix;
        set => SetField(ref _outputSuffix, value);
    }

    public bool ShowPreview
    {
        get => _showPreview;
        set => SetField(ref _showPreview, value);
    }

    public bool OptimizeDeadCode
    {
        get => _optimizeDeadCode;
        set => SetField(ref _optimizeDeadCode, value);
    }

    public bool EvaluateConstantExpressions
    {
        get => _evaluateConstantExpressions;
        set => SetField(ref _evaluateConstantExpressions, value);
    }

    public bool RemoveUnusedUniforms
    {
        get => _removeUnusedUniforms;
        set => SetField(ref _removeUnusedUniforms, value);
    }

    public bool InlineSingleUseConstants
    {
        get => _inlineSingleUseConstants;
        set => SetField(ref _inlineSingleUseConstants, value);
    }

    #endregion

    #region Lifecycle

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RemakeUI();
    }

    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        SceneNode.Transform.Clear();
    }

    private void RemakeUI()
    {
        // UI will be rendered via ImGui in the actual implementation
        // This is a placeholder for component-based UI
    }

    #endregion

    #region Material/Shader Handling

    private void OnMaterialChanged()
    {
        _uniforms.Clear();
        if (_selectedMaterial == null)
            return;

        // Extract uniforms from material parameters
        foreach (var param in _selectedMaterial.Parameters)
        {
            if (param != null)
            {
                var info = new UniformLockInfo
                {
                    Name = param.Name,
                    Type = param.TypeName,
                    Value = param.GenericValue,
                    IsAnimated = IsUniformAnimated(param.Name),
                    ShaderVar = param
                };
                _uniforms[param.Name] = info;
            }
        }

        // If we have shaders, also extract uniform declarations from source
        if (_selectedMaterial.Shaders.Count > 0)
            SelectedShader = _selectedMaterial.Shaders[0];

        UpdatePreview();
    }

    private void OnShaderChanged()
    {
        if (_selectedShader?.Source?.Text == null)
            return;

        // Parse uniforms from shader source
        ParseUniformsFromSource(_selectedShader.Source.Text);
        UpdatePreview();
    }

    private void ParseUniformsFromSource(string source)
    {
        // Match uniform declarations: uniform type name;
        // Also handles arrays: uniform type name[size];
        var uniformRegex = UniformDeclarationRegex();

        foreach (Match match in uniformRegex.Matches(source))
        {
            string type = match.Groups["type"].Value;
            string name = match.Groups["name"].Value;
            string arraySize = match.Groups["array"].Value;

            if (!_uniforms.ContainsKey(name))
            {
                _uniforms[name] = new UniformLockInfo
                {
                    Name = name,
                    TypeString = type,
                    ArraySize = string.IsNullOrEmpty(arraySize) ? 0 : int.Parse(arraySize),
                    IsAnimated = IsUniformAnimated(name),
                    Value = GetDefaultValueForType(type)
                };
            }
        }
    }

    private bool IsUniformAnimated(string uniformName)
    {
        foreach (var pattern in _animatedUniformPatterns)
        {
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            if (Regex.IsMatch(uniformName, regex, RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }

    private static object GetDefaultValueForType(string type)
    {
        return type switch
        {
            "float" => 0.0f,
            "int" => 0,
            "uint" => 0u,
            "bool" => false,
            "vec2" => Vector2.Zero,
            "vec3" => Vector3.Zero,
            "vec4" => Vector4.Zero,
            "ivec2" => new int[] { 0, 0 },
            "ivec3" => new int[] { 0, 0, 0 },
            "ivec4" => new int[] { 0, 0, 0, 0 },
            "mat3" => Matrix3x2.Identity,
            "mat4" => Matrix4x4.Identity,
            _ => 0.0f
        };
    }

    #endregion

    #region Shader Locking

    /// <summary>
    /// Generates a locked version of the shader with non-animated uniforms replaced by constants.
    /// </summary>
    public string GenerateLockedShader(string source, Dictionary<string, UniformLockInfo> uniforms)
    {
        var result = new StringBuilder(source);

        // Step 1: Replace uniform declarations with const declarations for non-animated uniforms
        foreach (var kvp in uniforms.Where(u => !u.Value.IsAnimated))
        {
            var info = kvp.Value;
            string constValue = FormatValueAsGLSL(info.Value, info.TypeString ?? GetGLSLType(info.Type));

            // Pattern to match the uniform declaration
            string uniformPattern = $@"uniform\s+{Regex.Escape(info.TypeString ?? GetGLSLType(info.Type))}\s+{Regex.Escape(info.Name)}\s*;";
            string constDeclaration = $"const {info.TypeString ?? GetGLSLType(info.Type)} {info.Name} = {constValue};";

            result = new StringBuilder(Regex.Replace(result.ToString(), uniformPattern, constDeclaration));
        }

        string processed = result.ToString();

        // Step 2: Evaluate constant conditionals
        if (_evaluateConstantExpressions)
        {
            processed = EvaluateConstantConditionals(processed, uniforms);
        }

        // Step 3: Remove dead code branches
        if (_optimizeDeadCode)
        {
            processed = RemoveDeadCodeBranches(processed);
        }

        // Step 4: Remove unused uniform declarations
        if (_removeUnusedUniforms)
        {
            processed = RemoveUnusedUniformDeclarations(processed);
        }

        // Step 5: Inline single-use constants
        if (_inlineSingleUseConstants)
        {
            processed = InlineConstants(processed);
        }

        return processed;
    }

    /// <summary>
    /// Evaluates constant conditionals like: if (_EnableFeature > 0.5) { ... }
    /// where _EnableFeature is a locked constant.
    /// </summary>
    private string EvaluateConstantConditionals(string source, Dictionary<string, UniformLockInfo> uniforms)
    {
        var result = source;

        // Find if statements with simple comparisons to constants
        var ifPattern = IfStatementRegex();

        foreach (Match match in ifPattern.Matches(source))
        {
            string varName = match.Groups["var"].Value;
            string op = match.Groups["op"].Value;
            string valueStr = match.Groups["value"].Value;

            if (uniforms.TryGetValue(varName, out var info) && !info.IsAnimated)
            {
                // Try to evaluate the condition
                bool? conditionResult = EvaluateCondition(info.Value, op, valueStr);
                if (conditionResult.HasValue)
                {
                    // Mark this condition for replacement
                    // We'll replace with: if (true) or if (false)
                    string replacement = conditionResult.Value ? "if (true)" : "if (false)";
                    result = result.Replace(match.Value, replacement);
                }
            }
        }

        return result;
    }

    private static bool? EvaluateCondition(object value, string op, string compareValue)
    {
        try
        {
            double left = Convert.ToDouble(value);
            double right = double.Parse(compareValue);

            return op switch
            {
                ">" => left > right,
                "<" => left < right,
                ">=" => left >= right,
                "<=" => left <= right,
                "==" => Math.Abs(left - right) < 0.0001,
                "!=" => Math.Abs(left - right) >= 0.0001,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes dead code branches like: if (false) { ... } or the else of if (true) { ... }
    /// </summary>
    private static string RemoveDeadCodeBranches(string source)
    {
        var result = source;

        // Remove if (false) { ... } blocks including any else
        result = Regex.Replace(result, @"if\s*\(\s*false\s*\)\s*\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}(?:\s*else\s*\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\})?", 
            m => m.Groups[1].Success ? m.Groups[1].Value : "", 
            RegexOptions.Singleline);

        // Convert if (true) { ... } else { ... } to just the true block content
        result = Regex.Replace(result, @"if\s*\(\s*true\s*\)\s*\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}\s*else\s*\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}",
            m => m.Groups[1].Value,
            RegexOptions.Singleline);

        // Convert if (true) { ... } (no else) to just the block content  
        result = Regex.Replace(result, @"if\s*\(\s*true\s*\)\s*\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}",
            m => m.Groups[1].Value,
            RegexOptions.Singleline);

        return result;
    }

    /// <summary>
    /// Removes uniform declarations that are no longer used in the shader.
    /// </summary>
    private static string RemoveUnusedUniformDeclarations(string source)
    {
        var result = source;

        // Find all uniform declarations
        var uniformDecls = Regex.Matches(source, @"uniform\s+\w+\s+(\w+)\s*(?:\[[^\]]*\])?\s*;");

        foreach (Match decl in uniformDecls)
        {
            string uniformName = decl.Groups[1].Value;

            // Count usages (excluding the declaration itself)
            string sourceWithoutDecl = source.Replace(decl.Value, "");
            int usageCount = Regex.Matches(sourceWithoutDecl, $@"\b{Regex.Escape(uniformName)}\b").Count;

            if (usageCount == 0)
            {
                // Remove the entire declaration line
                result = result.Replace(decl.Value, "// REMOVED: " + decl.Value.Trim());
            }
        }

        return result;
    }

    /// <summary>
    /// Inlines const declarations that are only used once.
    /// </summary>
    private static string InlineConstants(string source)
    {
        var result = source;

        // Find const declarations
        var constDecls = Regex.Matches(source, @"const\s+(\w+)\s+(\w+)\s*=\s*([^;]+);");

        foreach (Match decl in constDecls)
        {
            string constType = decl.Groups[1].Value;
            string constName = decl.Groups[2].Value;
            string constValue = decl.Groups[3].Value.Trim();

            // Count usages (excluding the declaration itself)
            string sourceWithoutDecl = source.Replace(decl.Value, "");
            var usages = Regex.Matches(sourceWithoutDecl, $@"\b{Regex.Escape(constName)}\b");

            // Only inline if used exactly once and value is simple
            if (usages.Count == 1 && IsSimpleValue(constValue))
            {
                // Remove declaration and inline the value
                result = result.Replace(decl.Value, "");
                result = Regex.Replace(result, $@"\b{Regex.Escape(constName)}\b", constValue);
            }
        }

        return result;
    }

    private static bool IsSimpleValue(string value)
    {
        // Simple values: literals, single identifiers, or basic constructor calls
        return Regex.IsMatch(value, @"^[\d.]+f?$") ||  // Number
               Regex.IsMatch(value, @"^(true|false)$") ||  // Boolean
               Regex.IsMatch(value, @"^vec[234]\s*\([^()]*\)$") ||  // Vector constructor
               Regex.IsMatch(value, @"^\w+$");  // Single identifier
    }

    #endregion

    #region GLSL Formatting

    private static string FormatValueAsGLSL(object? value, string type)
    {
        if (value == null)
            return GetDefaultGLSLValue(type);

        return type switch
        {
            "float" => FormatFloat(Convert.ToSingle(value)),
            "int" => value.ToString() ?? "0",
            "uint" => $"{value}u",
            "bool" => value.ToString()?.ToLower() ?? "false",
            "vec2" => FormatVec2(value),
            "vec3" => FormatVec3(value),
            "vec4" => FormatVec4(value),
            "ivec2" => FormatIVec2(value),
            "ivec3" => FormatIVec3(value),
            "ivec4" => FormatIVec4(value),
            "mat4" => FormatMat4(value),
            _ => value.ToString() ?? GetDefaultGLSLValue(type)
        };
    }

    private static string FormatFloat(float value)
    {
        string str = value.ToString("G9");
        if (!str.Contains('.') && !str.Contains('E') && !str.Contains('e'))
            str += ".0";
        return str;
    }

    private static string FormatVec2(object value)
    {
        if (value is Vector2 v)
            return $"vec2({FormatFloat(v.X)}, {FormatFloat(v.Y)})";
        return "vec2(0.0, 0.0)";
    }

    private static string FormatVec3(object value)
    {
        if (value is Vector3 v)
            return $"vec3({FormatFloat(v.X)}, {FormatFloat(v.Y)}, {FormatFloat(v.Z)})";
        return "vec3(0.0, 0.0, 0.0)";
    }

    private static string FormatVec4(object value)
    {
        if (value is Vector4 v)
            return $"vec4({FormatFloat(v.X)}, {FormatFloat(v.Y)}, {FormatFloat(v.Z)}, {FormatFloat(v.W)})";
        return "vec4(0.0, 0.0, 0.0, 0.0)";
    }

    private static string FormatIVec2(object value)
    {
        if (value is int[] arr && arr.Length >= 2)
            return $"ivec2({arr[0]}, {arr[1]})";
        return "ivec2(0, 0)";
    }

    private static string FormatIVec3(object value)
    {
        if (value is int[] arr && arr.Length >= 3)
            return $"ivec3({arr[0]}, {arr[1]}, {arr[2]})";
        return "ivec3(0, 0, 0)";
    }

    private static string FormatIVec4(object value)
    {
        if (value is int[] arr && arr.Length >= 4)
            return $"ivec4({arr[0]}, {arr[1]}, {arr[2]}, {arr[3]})";
        return "ivec4(0, 0, 0, 0)";
    }

    private static string FormatMat4(object value)
    {
        if (value is Matrix4x4 m)
        {
            return $"mat4(" +
                   $"{FormatFloat(m.M11)}, {FormatFloat(m.M21)}, {FormatFloat(m.M31)}, {FormatFloat(m.M41)}, " +
                   $"{FormatFloat(m.M12)}, {FormatFloat(m.M22)}, {FormatFloat(m.M32)}, {FormatFloat(m.M42)}, " +
                   $"{FormatFloat(m.M13)}, {FormatFloat(m.M23)}, {FormatFloat(m.M33)}, {FormatFloat(m.M43)}, " +
                   $"{FormatFloat(m.M14)}, {FormatFloat(m.M24)}, {FormatFloat(m.M34)}, {FormatFloat(m.M44)})";
        }
        return "mat4(1.0)";
    }

    private static string GetDefaultGLSLValue(string type)
    {
        return type switch
        {
            "float" => "0.0",
            "int" => "0",
            "uint" => "0u",
            "bool" => "false",
            "vec2" => "vec2(0.0)",
            "vec3" => "vec3(0.0)",
            "vec4" => "vec4(0.0)",
            "ivec2" => "ivec2(0)",
            "ivec3" => "ivec3(0)",
            "ivec4" => "ivec4(0)",
            "mat3" => "mat3(1.0)",
            "mat4" => "mat4(1.0)",
            _ => "0.0"
        };
    }

    private static string GetGLSLType(EShaderVarType type)
    {
        return type switch
        {
            EShaderVarType._float => "float",
            EShaderVarType._int => "int",
            EShaderVarType._uint => "uint",
            EShaderVarType._bool => "bool",
            EShaderVarType._vec2 => "vec2",
            EShaderVarType._vec3 => "vec3",
            EShaderVarType._vec4 => "vec4",
            EShaderVarType._ivec2 => "ivec2",
            EShaderVarType._ivec3 => "ivec3",
            EShaderVarType._ivec4 => "ivec4",
            EShaderVarType._uvec2 => "uvec2",
            EShaderVarType._uvec3 => "uvec3",
            EShaderVarType._uvec4 => "uvec4",
            EShaderVarType._mat3 => "mat3",
            EShaderVarType._mat4 => "mat4",
            EShaderVarType._dvec2 => "dvec2",
            EShaderVarType._dvec3 => "dvec3",
            EShaderVarType._dvec4 => "dvec4",
            EShaderVarType._bvec2 => "bvec2",
            EShaderVarType._bvec3 => "bvec3",
            EShaderVarType._bvec4 => "bvec4",
            _ => "float"
        };
    }

    #endregion

    #region Preview

    private void UpdatePreview()
    {
        if (_selectedShader?.Source?.Text == null)
        {
            _previewText = "";
            return;
        }

        _previewText = GenerateLockedShader(_selectedShader.Source.Text, _uniforms);
    }

    public string GetPreviewText() => _previewText;

    #endregion

    #region Export

    /// <summary>
    /// Exports the locked shader to a file.
    /// </summary>
    public bool ExportLockedShader(string outputPath)
    {
        if (string.IsNullOrEmpty(_previewText))
        {
            UpdatePreview();
            if (string.IsNullOrEmpty(_previewText))
                return false;
        }

        try
        {
            // Add header comment
            var header = new StringBuilder();
            header.AppendLine("// ============================================");
            header.AppendLine("// LOCKED SHADER - Auto-generated");
            header.AppendLine($"// Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            header.AppendLine($"// Source: {_selectedShader?.Source?.FilePath ?? "Unknown"}");
            header.AppendLine("// ");
            header.AppendLine("// Locked uniforms (converted to constants):");
            foreach (var kvp in _uniforms.Where(u => !u.Value.IsAnimated))
            {
                header.AppendLine($"//   {kvp.Key} = {kvp.Value.Value}");
            }
            header.AppendLine("// ");
            header.AppendLine("// Animated uniforms (kept as uniforms):");
            foreach (var kvp in _uniforms.Where(u => u.Value.IsAnimated))
            {
                header.AppendLine($"//   {kvp.Key}");
            }
            header.AppendLine("// ============================================");
            header.AppendLine();

            string finalContent = header.ToString() + _previewText;
            File.WriteAllText(outputPath, finalContent);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to export locked shader: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Exports all shaders from the selected material.
    /// </summary>
    public void ExportAllShadersFromMaterial(string outputDirectory)
    {
        if (_selectedMaterial == null)
            return;

        Directory.CreateDirectory(outputDirectory);

        foreach (var shader in _selectedMaterial.Shaders)
        {
            if (shader?.Source?.Text == null)
                continue;

            SelectedShader = shader;
            UpdatePreview();

            string fileName = Path.GetFileNameWithoutExtension(shader.Source.FilePath ?? "shader");
            string extension = Path.GetExtension(shader.Source.FilePath ?? ".glsl");
            string outputPath = Path.Combine(outputDirectory, $"{fileName}{_outputSuffix}{extension}");

            ExportLockedShader(outputPath);
        }
    }

    #endregion

    #region Uniform Management

    public void SetUniformAnimated(string name, bool isAnimated)
    {
        if (_uniforms.TryGetValue(name, out var info))
        {
            info.IsAnimated = isAnimated;
            UpdatePreview();
        }
    }

    public void SetUniformValue(string name, object value)
    {
        if (_uniforms.TryGetValue(name, out var info))
        {
            info.Value = value;
            UpdatePreview();
        }
    }

    public void AddAnimatedPattern(string pattern)
    {
        if (!_animatedUniformPatterns.Contains(pattern))
        {
            _animatedUniformPatterns.Add(pattern);
            RefreshAnimatedFlags();
        }
    }

    public void RemoveAnimatedPattern(string pattern)
    {
        if (_animatedUniformPatterns.Remove(pattern))
        {
            RefreshAnimatedFlags();
        }
    }

    public IReadOnlyList<string> GetAnimatedPatterns() => _animatedUniformPatterns;

    private void RefreshAnimatedFlags()
    {
        foreach (var kvp in _uniforms)
        {
            kvp.Value.IsAnimated = IsUniformAnimated(kvp.Key);
        }
        UpdatePreview();
    }

    public IReadOnlyDictionary<string, UniformLockInfo> GetUniforms() => _uniforms;

    #endregion

    #region Generated Regex

    [GeneratedRegex(@"uniform\s+(?<type>\w+)\s+(?<name>\w+)(?:\[(?<array>\d+)\])?\s*;")]
    private static partial Regex UniformDeclarationRegex();

    [GeneratedRegex(@"if\s*\(\s*(?<var>\w+)\s*(?<op>[<>=!]+)\s*(?<value>[\d.]+f?)\s*\)")]
    private static partial Regex IfStatementRegex();

    #endregion
}

/// <summary>
/// Information about a uniform that can be locked.
/// </summary>
public class UniformLockInfo
{
    public string Name { get; set; } = "";
    public EShaderVarType Type { get; set; }
    public string? TypeString { get; set; }
    public object? Value { get; set; }
    public bool IsAnimated { get; set; }
    public int ArraySize { get; set; }
    public ShaderVar? ShaderVar { get; set; }

    /// <summary>
    /// Description/tooltip for the uniform.
    /// </summary>
    public string Description { get; set; } = "";
}
