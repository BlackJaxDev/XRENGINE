using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.Shaders;

namespace XREngine.Rendering.Vulkan;

public readonly record struct DescriptorBindingInfo(
    uint Set,
    uint Binding,
    DescriptorType DescriptorType,
    ShaderStageFlags StageFlags,
    uint Count,
    string Name);

public readonly record struct AutoUniformMember(
    string Name,
    string GlslType,
    EShaderVarType? EngineType,
    bool IsArray,
    uint ArrayLength,
    uint ArrayStride,
    uint Offset,
    uint Size,
    AutoUniformDefaultValue? DefaultValue,
    IReadOnlyList<AutoUniformDefaultValue>? DefaultArrayValues);

public readonly record struct AutoUniformDefaultValue(
    EShaderVarType Type,
    object Value);

public sealed record AutoUniformBlockInfo(
    string BlockName,
    string InstanceName,
    uint Set,
    uint Binding,
    uint Size,
    IReadOnlyList<AutoUniformMember> Members,
    EShaderType ShaderType);

internal readonly record struct AutoUniformRewriteResult(
    string Source,
    AutoUniformBlockInfo? BlockInfo);

internal static class VulkanShaderAutoUniforms
{
    private static readonly Regex FloatSuffixRegex = new(
        @"(?<![A-Za-z_])(?<num>(?:\d+\.\d*|\d*\.\d+|\d+)(?:[eE][+-]?\d+)?)[fF]\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UniformStatementRegex = new(
        @"^\s*(?:layout\s*\([^)]*\)\s*)?uniform\s+(?<statement>[^;]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex ArrayRegex = new(@"\[(?<size>[A-Za-z_][A-Za-z0-9_]*|\d+u?)\]", RegexOptions.Compiled);
    private static readonly Regex ConstIntegralRegex = new(
        @"\bconst\s+(?:uint|int)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>\d+)u?\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex DefineIntegralRegex = new(
        @"^\s*#\s*define\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+(?<value>\d+)u?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex LayoutQualifierRegex = new(
        @"layout\s*\((?<qualifiers>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex OpaqueUniformRegex = new(
        @"^\s*(?<layout>layout\s*\([^)]*\)\s*)?uniform\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<declaration>[^;{]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex StructDeclarationRegex = new(
        @"\bstruct\s+[A-Za-z_][A-Za-z0-9_]*\s*\{[\s\S]*?\};",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> OpaqueTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "sampler1D",
        "sampler2D",
        "sampler3D",
        "samplerCube",
        "sampler2DArray",
        "samplerCubeArray",
        "sampler1DShadow",
        "sampler2DShadow",
        "samplerCubeShadow",
        "sampler2DArrayShadow",
        "samplerCubeArrayShadow",
        "samplerBuffer",
        "image1D",
        "image2D",
        "image3D",
        "imageCube",
        "image2DArray",
        "imageCubeArray",
        "imageBuffer",
        "iimage1D",
        "iimage2D",
        "iimage3D",
        "iimageCube",
        "iimage2DArray",
        "iimageCubeArray",
        "iimageBuffer",
        "uimage1D",
        "uimage2D",
        "uimage3D",
        "uimageCube",
        "uimage2DArray",
        "uimageCubeArray",
        "uimageBuffer",
        "subpassInput",
        "subpassInputMS",
        "atomic_uint"
    };

    private static readonly Dictionary<string, EShaderVarType> GlslTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = EShaderVarType._bool,
        ["bvec2"] = EShaderVarType._bvec2,
        ["bvec3"] = EShaderVarType._bvec3,
        ["bvec4"] = EShaderVarType._bvec4,
        ["int"] = EShaderVarType._int,
        ["ivec2"] = EShaderVarType._ivec2,
        ["ivec3"] = EShaderVarType._ivec3,
        ["ivec4"] = EShaderVarType._ivec4,
        ["uint"] = EShaderVarType._uint,
        ["uvec2"] = EShaderVarType._uvec2,
        ["uvec3"] = EShaderVarType._uvec3,
        ["uvec4"] = EShaderVarType._uvec4,
        ["float"] = EShaderVarType._float,
        ["vec2"] = EShaderVarType._vec2,
        ["vec3"] = EShaderVarType._vec3,
        ["vec4"] = EShaderVarType._vec4,
        ["double"] = EShaderVarType._double,
        ["dvec2"] = EShaderVarType._dvec2,
        ["dvec3"] = EShaderVarType._dvec3,
        ["dvec4"] = EShaderVarType._dvec4,
        ["mat3"] = EShaderVarType._mat3,
        ["mat4"] = EShaderVarType._mat4
    };

    public static AutoUniformRewriteResult Rewrite(string source, EShaderType shaderType)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new AutoUniformRewriteResult(source, null);

        source = ApplyVulkanSourceFixups(source);

        bool enableAutoUniformRewrite = !string.Equals(
            Environment.GetEnvironmentVariable("XRE_VK_ENABLE_AUTO_UNIFORM_REWRITE"),
            "0",
            StringComparison.Ordinal);

        if (!enableAutoUniformRewrite)
            return new AutoUniformRewriteResult(RewriteOpaqueUniformBindings(source, shaderType), null);

        Dictionary<string, uint> integralConstants = ParseIntegralConstants(source);

        List<(string GlslType, string Name, bool IsArray, uint ArrayLength, AutoUniformDefaultValue? DefaultValue, IReadOnlyList<AutoUniformDefaultValue>? DefaultArrayValues)> members = new();
        StringBuilder output = new(source.Length + 256);

        int lastIndex = 0;
        foreach (Match match in UniformStatementRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string statement = match.Groups["statement"].Value;
            if (statement.IndexOf('{') >= 0)
                continue; // uniform block

            bool canRewriteStatement = false;
            var statementMembers = new List<(string GlslType, string Name, bool IsArray, uint ArrayLength, AutoUniformDefaultValue? DefaultValue, IReadOnlyList<AutoUniformDefaultValue>? DefaultArrayValues)>();

            if (!TryExtractTypeAndDeclarators(statement, out string glslType, out string declarators))
                continue;

            if (IsOpaque(glslType))
                continue;

            bool allDeclaratorsParsed = true;

            foreach (string declarator in SplitDeclarators(declarators))
            {
                if (!TryParseDeclarator(declarator, integralConstants, out string name, out bool isArray, out uint arrayLength, out string? defaultExpression))
                {
                    allDeclaratorsParsed = false;
                    break;
                }

                AutoUniformDefaultValue? defaultValue = null;
                IReadOnlyList<AutoUniformDefaultValue>? defaultArrayValues = null;
                if (!string.IsNullOrWhiteSpace(defaultExpression))
                {
                    if (isArray && TryParseDefaultArray(glslType, defaultExpression!, arrayLength, out var parsedArray))
                        defaultArrayValues = parsedArray;
                    else if (TryParseDefaultValue(glslType, defaultExpression!, out var parsed))
                        defaultValue = parsed;
                }

                statementMembers.Add((glslType, name, isArray, arrayLength, defaultValue, defaultArrayValues));
            }

            if (!allDeclaratorsParsed)
                continue;

            if (statementMembers.Count > 0)
            {
                canRewriteStatement = true;
                members.AddRange(statementMembers);
            }

            if (!canRewriteStatement)
                continue;

            output.Append(source, lastIndex, match.Index - lastIndex);
            lastIndex = match.Index + match.Length;
        }

        output.Append(source, lastIndex, source.Length - lastIndex);
        string rewritten = output.ToString();
        rewritten = RewriteOpaqueUniformBindings(rewritten, shaderType);

        if (members.Count == 0)
            return new AutoUniformRewriteResult(rewritten, null);

        string blockName = GetAutoUniformBlockName(shaderType);
        string instanceName = $"{blockName}_Instance";

        if (!TryComputeBlockLayout(members, out var layoutMembers, out uint blockSize))
            return new AutoUniformRewriteResult(source, null);

        uint binding = Math.Max(FindNextBinding(rewritten), 64u);

        foreach (var member in layoutMembers)
        {
            rewritten = Regex.Replace(
                rewritten,
                $@"\b{Regex.Escape(member.Name)}\b",
                $"{instanceName}.{member.Name}");
        }

        string block = BuildUniformBlock(blockName, instanceName, binding, layoutMembers);
        rewritten = InsertAfterStructOrVersion(rewritten, block);

        AutoUniformBlockInfo blockInfo = new(
            blockName,
            instanceName,
            0,
            binding,
            blockSize,
            layoutMembers,
            shaderType);

        return new AutoUniformRewriteResult(rewritten, blockInfo);
    }

    private static string BuildUniformBlock(string blockName, string instanceName, uint binding, IReadOnlyList<AutoUniformMember> members)
    {
        StringBuilder builder = new();
        builder.AppendLine($"layout(std140, set = {VulkanRenderer.DescriptorSetGlobals}, binding = {binding}) uniform {blockName}");
        builder.AppendLine("{");
        foreach (var member in members)
        {
            if (member.IsArray && member.ArrayLength > 0)
                builder.AppendLine($"    {member.GlslType} {member.Name}[{member.ArrayLength}];");
            else
                builder.AppendLine($"    {member.GlslType} {member.Name};");
        }
        builder.AppendLine($"}} {instanceName};");
        return builder.ToString();
    }

    private static string InsertAfterVersion(string source, string block)
    {
        using StringReader reader = new(source);
        StringBuilder builder = new(source.Length + block.Length + 16);
        bool inserted = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            builder.AppendLine(line);
            if (!inserted && line.TrimStart().StartsWith("#version", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine(block);
                inserted = true;
            }
        }

        if (!inserted)
        {
            return block + Environment.NewLine + source;
        }

        return builder.ToString();
    }

    private static string InsertAfterStructOrVersion(string source, string block)
    {
        int lastStructEnd = -1;
        foreach (Match match in StructDeclarationRegex.Matches(source))
        {
            if (match.Success)
                lastStructEnd = Math.Max(lastStructEnd, match.Index + match.Length);
        }

        if (lastStructEnd >= 0)
        {
            string prefix = source[..lastStructEnd];
            string suffix = source[lastStructEnd..];
            StringBuilder builder = new(source.Length + block.Length + 16);
            builder.Append(prefix);
            if (!prefix.EndsWith('\n'))
                builder.AppendLine();
            builder.AppendLine(block);
            if (!suffix.StartsWith('\n') && suffix.Length > 0)
                builder.AppendLine();
            builder.Append(suffix);
            return builder.ToString();
        }

        return InsertAfterVersion(source, block);
    }

    private static string InsertAtPreferredLocation(string source, string block, int insertionIndex)
    {
        if (insertionIndex >= 0 && insertionIndex <= source.Length)
        {
            string prefix = source[..insertionIndex];
            string suffix = source[insertionIndex..];

            StringBuilder builder = new(source.Length + block.Length + 16);
            builder.Append(prefix);

            if (!prefix.EndsWith('\n'))
                builder.AppendLine();

            builder.AppendLine(block);

            if (!suffix.StartsWith('\n') && suffix.Length > 0)
                builder.AppendLine();

            builder.Append(suffix);
            return builder.ToString();
        }

        return InsertAfterVersion(source, block);
    }

    private static bool TryExtractTypeAndDeclarators(string statement, out string glslType, out string declarators)
    {
        glslType = string.Empty;
        declarators = string.Empty;

        string trimmed = statement.Trim();
        if (trimmed.Length == 0)
            return false;

        string withoutLayout = LayoutQualifierRegex.Replace(trimmed, string.Empty).Trim();
        string[] tokens = withoutLayout.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            return false;

        glslType = tokens[0];
        declarators = withoutLayout[glslType.Length..].Trim();
        return true;
    }

    private static IEnumerable<string> SplitDeclarators(string declarators)
    {
        if (string.IsNullOrWhiteSpace(declarators))
            yield break;

        int bracketDepth = 0;
        int parenDepth = 0;
        int braceDepth = 0;
        int start = 0;
        for (int i = 0; i < declarators.Length; i++)
        {
            char c = declarators[i];
            if (c == '[')
                bracketDepth++;
            else if (c == ']')
                bracketDepth = Math.Max(0, bracketDepth - 1);
            else if (c == '(')
                parenDepth++;
            else if (c == ')')
                parenDepth = Math.Max(0, parenDepth - 1);
            else if (c == '{')
                braceDepth++;
            else if (c == '}')
                braceDepth = Math.Max(0, braceDepth - 1);
            else if (c == ',' && bracketDepth == 0 && parenDepth == 0 && braceDepth == 0)
            {
                if (i > start)
                    yield return declarators[start..i];
                start = i + 1;
            }
        }

        if (start < declarators.Length)
            yield return declarators[start..];
    }

    private static bool TryParseDeclarator(string declarator, IReadOnlyDictionary<string, uint> integralConstants, out string name, out bool isArray, out uint arrayLength, out string? defaultExpression)
    {
        name = string.Empty;
        isArray = false;
        arrayLength = 0;
        defaultExpression = null;

        if (string.IsNullOrWhiteSpace(declarator))
            return false;

        string trimmed = declarator.Trim();
        int equals = trimmed.IndexOf('=');
        if (equals >= 0)
        {
            defaultExpression = trimmed[(equals + 1)..].Trim();
            trimmed = trimmed[..equals].Trim();
        }

        Match arrayMatch = ArrayRegex.Match(trimmed);
        if (arrayMatch.Success)
        {
            string sizeToken = arrayMatch.Groups["size"].Value.Trim();
            sizeToken = sizeToken.TrimEnd('u', 'U');

            if (!uint.TryParse(sizeToken, out uint size) && !integralConstants.TryGetValue(sizeToken, out size))
                return false;

            isArray = true;
            arrayLength = size;
            trimmed = ArrayRegex.Replace(trimmed, string.Empty);
        }

        string[] tokens = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        name = tokens[^1];
        return !string.IsNullOrWhiteSpace(name);
    }

    private static string ApplyVulkanSourceFixups(string source)
    {
        string rewritten = source.Replace("gl_InstanceID", "gl_InstanceIndex", StringComparison.Ordinal);
        rewritten = FloatSuffixRegex.Replace(rewritten, "${num}");
        return rewritten;
    }

    private static string RewriteOpaqueUniformBindings(string source, EShaderType shaderType)
    {
        if (string.IsNullOrWhiteSpace(source))
            return source;

        uint nextBinding = Math.Max(FindNextBinding(source), GetOpaqueBindingBase(shaderType));
        return OpaqueUniformRegex.Replace(source, match =>
        {
            string glslType = match.Groups["type"].Value;
            if (!IsOpaque(glslType))
                return match.Value;

            string declaration = match.Groups["declaration"].Value.Trim();
            string existingLayout = match.Groups["layout"].Value;
            string layoutPrefix;
            if (!string.IsNullOrWhiteSpace(existingLayout))
            {
                bool hasBinding = existingLayout.Contains("binding", StringComparison.OrdinalIgnoreCase);
                layoutPrefix = hasBinding
                    ? EnsureLayoutHasSet(existingLayout, VulkanRenderer.DescriptorSetMaterial)
                    : $"layout(set = {VulkanRenderer.DescriptorSetMaterial}, binding = {nextBinding++}) ";
            }
            else
            {
                layoutPrefix = $"layout(set = {VulkanRenderer.DescriptorSetMaterial}, binding = {nextBinding++}) ";
            }

            return $"{layoutPrefix}uniform {glslType} {declaration};";
        });
    }

    private static string EnsureLayoutHasSet(string layout, uint set)
    {
        if (layout.Contains("set", StringComparison.OrdinalIgnoreCase))
            return layout;

        Match layoutMatch = LayoutQualifierRegex.Match(layout);
        if (!layoutMatch.Success)
            return layout;

        string qualifiers = layoutMatch.Groups["qualifiers"].Value.Trim();
        string updatedQualifiers = string.IsNullOrWhiteSpace(qualifiers)
            ? $"set = {set}"
            : $"{qualifiers}, set = {set}";

            return LayoutQualifierRegex.Replace(layout, $"layout({updatedQualifiers}) ", 1);
    }

    private static uint GetOpaqueBindingBase(EShaderType shaderType)
        => shaderType switch
        {
            EShaderType.Fragment => 0u,
            EShaderType.Vertex => 32u,
            EShaderType.Geometry => 40u,
            EShaderType.TessControl => 44u,
            EShaderType.TessEvaluation => 48u,
            EShaderType.Compute => 52u,
            EShaderType.Task => 56u,
            EShaderType.Mesh => 60u,
            _ => 32u
        };

    private static bool IsOpaque(string glslType)
    {
        if (OpaqueTypes.Contains(glslType))
            return true;

        return glslType.StartsWith("sampler", StringComparison.OrdinalIgnoreCase)
            || glslType.StartsWith("isampler", StringComparison.OrdinalIgnoreCase)
            || glslType.StartsWith("usampler", StringComparison.OrdinalIgnoreCase)
            || glslType.StartsWith("image", StringComparison.OrdinalIgnoreCase)
            || glslType.StartsWith("subpass", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, uint> ParseIntegralConstants(string source)
    {
        var constants = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (Match match in ConstIntegralRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string name = match.Groups["name"].Value;
            string valueText = match.Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(name) || !uint.TryParse(valueText, out uint value))
                continue;

            constants[name] = value;
        }

        foreach (Match match in DefineIntegralRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string name = match.Groups["name"].Value;
            string valueText = match.Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(name) || !uint.TryParse(valueText, out uint value))
                continue;

            constants[name] = value;
        }

        return constants;
    }

    private static uint FindNextBinding(string source)
    {
        uint max = 0;
        foreach (Match match in LayoutQualifierRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string qualifiers = match.Groups["qualifiers"].Value;
            if (!TryParseQualifier(qualifiers, "binding", out uint value))
                continue;

            if (value >= max)
                max = value + 1;
        }

        return max;
    }

    private static bool TryParseQualifier(string qualifiers, string key, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(qualifiers))
            return false;

        string[] parts = qualifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            int equals = part.IndexOf('=');
            if (equals < 0)
                continue;

            string qualifierKey = part[..equals].Trim();
            if (!qualifierKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            string rawValue = part[(equals + 1)..].Trim();
            if (uint.TryParse(rawValue, out value))
                return true;
        }

        return false;
    }

    private static string GetAutoUniformBlockName(EShaderType type)
        => $"XREngine_AutoUniforms_{type}";

    private static bool TryComputeBlockLayout(
        List<(string GlslType, string Name, bool IsArray, uint ArrayLength, AutoUniformDefaultValue? DefaultValue, IReadOnlyList<AutoUniformDefaultValue>? DefaultArrayValues)> members,
        out List<AutoUniformMember> layoutMembers,
        out uint blockSize)
    {
        layoutMembers = new List<AutoUniformMember>(members.Count);
        blockSize = 0;
        uint offset = 0;

        foreach (var (GlslType, Name, IsArray, ArrayLength, DefaultValue, DefaultArrayValues) in members)
        {
            if (!TryGetStd140Info(GlslType, IsArray, ArrayLength, out uint alignment, out uint size, out uint arrayStride, out EShaderVarType? engineType))
                return false;

            offset = Align(offset, alignment);
            layoutMembers.Add(new AutoUniformMember(Name, GlslType, engineType, IsArray, ArrayLength, arrayStride, offset, size, DefaultValue, DefaultArrayValues));
            offset += size;
        }

        blockSize = Align(offset, 16);
        return true;
    }

    private static uint Align(uint value, uint alignment)
        => alignment == 0 ? value : (uint)((value + alignment - 1) / alignment * alignment);

    private static bool TryGetStd140Info(string glslType, bool isArray, uint arrayLength, out uint alignment, out uint size, out uint arrayStride, out EShaderVarType? engineType)
    {
        alignment = 0;
        size = 0;
        arrayStride = 0;
        engineType = GlslTypeMap.TryGetValue(glslType, out var mapped) ? mapped : null;

        if (!TryGetStd140Base(glslType, out uint baseAlignment, out uint baseSize))
            return false;

        if (!isArray)
        {
            alignment = baseAlignment;
            size = baseSize;
            return true;
        }

        if (arrayLength == 0)
            return false;

        uint stride = Math.Max(baseAlignment, 16u);
        alignment = stride;
        arrayStride = stride;
        size = stride * arrayLength;
        return true;
    }

    private static bool TryParseDefaultValue(string glslType, string expression, out AutoUniformDefaultValue value)
    {
        value = default;
        string trimmed = expression.Trim().TrimEnd(';');

        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        string lowerType = glslType.ToLowerInvariant();
        switch (lowerType)
        {
            case "float":
                if (TryParseFloat(trimmed, out float f))
                {
                    value = new AutoUniformDefaultValue(EShaderVarType._float, f);
                    return true;
                }
                return false;
            case "int":
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                {
                    value = new AutoUniformDefaultValue(EShaderVarType._int, i);
                    return true;
                }
                return false;
            case "uint":
                if (uint.TryParse(trimmed.TrimEnd('u', 'U'), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u))
                {
                    value = new AutoUniformDefaultValue(EShaderVarType._uint, u);
                    return true;
                }
                return false;
            case "bool":
                if (bool.TryParse(trimmed, out bool b))
                {
                    value = new AutoUniformDefaultValue(EShaderVarType._bool, b ? 1 : 0);
                    return true;
                }
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bi))
                {
                    value = new AutoUniformDefaultValue(EShaderVarType._bool, bi != 0 ? 1 : 0);
                    return true;
                }
                return false;
            case "vec2":
                if (TryParseVector(trimmed, "vec2", 2, out float[] v2))
                {
                    value = new AutoUniformDefaultValue(EShaderVarType._vec2, new Vector2(v2[0], v2[1]));
                    return true;
                }
                return false;
            case "vec3":
                if (TryParseVector(trimmed, "vec3", 3, out float[] v3))
                {
                    value = new AutoUniformDefaultValue(EShaderVarType._vec3, new Vector3(v3[0], v3[1], v3[2]));
                    return true;
                }
                return false;
            case "vec4":
                if (TryParseVector(trimmed, "vec4", 4, out float[] v4))
                {
                    value = new AutoUniformDefaultValue(EShaderVarType._vec4, new Vector4(v4[0], v4[1], v4[2], v4[3]));
                    return true;
                }
                return false;
            case "mat4":
                if (TryParseMatrix(trimmed, "mat4", 4, out Matrix4x4 m4))
                {
                    value = new AutoUniformDefaultValue(EShaderVarType._mat4, m4);
                    return true;
                }
                return false;
        }

        return false;
    }

    private static bool TryParseDefaultArray(string glslType, string expression, uint arrayLength, out IReadOnlyList<AutoUniformDefaultValue> values)
    {
        values = [];
        if (arrayLength == 0)
            return false;

        string trimmed = expression.Trim().TrimEnd(';');
        if (trimmed.Length == 0)
            return false;

        string inner;
        if (trimmed.StartsWith('{'))
        {
            int end = trimmed.LastIndexOf('}');
            if (end <= 0)
                return false;
            inner = trimmed[1..end];
        }
        else
        {
            string ctor = glslType + "[]";
            if (!trimmed.StartsWith(ctor, StringComparison.OrdinalIgnoreCase))
                return false;

            int start = trimmed.IndexOf('(');
            int end = trimmed.LastIndexOf(')');
            if (start < 0 || end <= start)
                return false;
            inner = trimmed[(start + 1)..end];
        }

        List<AutoUniformDefaultValue> parsed = [];
        foreach (string item in SplitArrayElements(inner))
        {
            if (TryParseDefaultValue(glslType, item, out var value))
                parsed.Add(value);
        }

        if (parsed.Count == 1 && arrayLength > 1)
        {
            AutoUniformDefaultValue single = parsed[0];
            parsed.Clear();
            for (int i = 0; i < arrayLength; i++)
                parsed.Add(single);
        }

        values = parsed;
        return parsed.Count > 0;
    }

    private static IEnumerable<string> SplitArrayElements(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            yield break;

        int depth = 0;
        int start = 0;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '(' || c == '{')
                depth++;
            else if (c == ')' || c == '}')
                depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                if (i > start)
                    yield return source[start..i].Trim();
                start = i + 1;
            }
        }

        if (start < source.Length)
            yield return source[start..].Trim();
    }

    private static bool TryParseFloat(string raw, out float value)
    {
        string sanitized = raw.Trim();
        if (sanitized.EndsWith('f') || sanitized.EndsWith('F'))
            sanitized = sanitized[..^1];

        return float.TryParse(sanitized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseVector(string expression, string constructor, int length, out float[] values)
    {
        values = [];
        if (!expression.StartsWith(constructor, StringComparison.OrdinalIgnoreCase))
            return false;

        int start = expression.IndexOf('(');
        int end = expression.LastIndexOf(')');
        if (start < 0 || end <= start)
            return false;

        string inner = expression[(start + 1)..end];
        string[] parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        float[] parsed = new float[length];
        if (parts.Length == 1 && TryParseFloat(parts[0], out float scalar))
        {
            for (int i = 0; i < length; i++)
                parsed[i] = scalar;
            values = parsed;
            return true;
        }

        for (int i = 0; i < length; i++)
            parsed[i] = i < parts.Length && TryParseFloat(parts[i], out float component) ? component : 0f;

        values = parsed;
        return true;
    }

    private static bool TryParseMatrix(string expression, string constructor, int dimension, out Matrix4x4 value)
    {
        value = Matrix4x4.Identity;
        if (!expression.StartsWith(constructor, StringComparison.OrdinalIgnoreCase))
            return false;

        int start = expression.IndexOf('(');
        int end = expression.LastIndexOf(')');
        if (start < 0 || end <= start)
            return false;

        string inner = expression[(start + 1)..end];
        string[] parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        if (parts.Length == 1 && TryParseFloat(parts[0], out float scalar))
        {
            value = new Matrix4x4(
                scalar, 0, 0, 0,
                0, scalar, 0, 0,
                0, 0, scalar, 0,
                0, 0, 0, scalar);
            return true;
        }

        if (parts.Length < dimension * dimension)
            return false;

        float[] vals = new float[dimension * dimension];
        for (int i = 0; i < vals.Length; i++)
        {
            if (!TryParseFloat(parts[i], out float component))
                return false;
            vals[i] = component;
        }

        value = new Matrix4x4(
            vals[0], vals[1], vals[2], vals[3],
            vals[4], vals[5], vals[6], vals[7],
            vals[8], vals[9], vals[10], vals[11],
            vals[12], vals[13], vals[14], vals[15]);
        return true;
    }

    private static bool TryGetStd140Base(string glslType, out uint alignment, out uint size)
    {
        alignment = 0;
        size = 0;

        switch (glslType.ToLowerInvariant())
        {
            case "bool":
            case "int":
            case "uint":
            case "float":
                alignment = 4;
                size = 4;
                return true;
            case "double":
                alignment = 8;
                size = 8;
                return true;
            case "vec2":
            case "ivec2":
            case "uvec2":
            case "bvec2":
                alignment = 8;
                size = 8;
                return true;
            case "dvec2":
                alignment = 16;
                size = 16;
                return true;
            case "vec3":
            case "vec4":
            case "ivec3":
            case "ivec4":
            case "uvec3":
            case "uvec4":
            case "bvec3":
            case "bvec4":
                alignment = 16;
                size = 16;
                return true;
            case "dvec3":
            case "dvec4":
                alignment = 32;
                size = 32;
                return true;
            case "mat3":
                alignment = 16;
                size = 48;
                return true;
            case "mat4":
                alignment = 16;
                size = 64;
                return true;
            default:
                alignment = 16;
                size = 64;
                return true;
        }
    }
}

internal static class VulkanShaderCompiler
{
    private static readonly Shaderc ShadercApi = Shaderc.GetApi();
    private static readonly Regex IncludeRegex = new(
        @"^\s*#\s*include\s+[""<](?<path>[^"">]+)["">]\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static unsafe byte[] Compile(
        XRShader shader,
        out string entryPoint,
        out AutoUniformBlockInfo? autoUniformBlock,
        out string? rewrittenSource)
    {
        entryPoint = "main";
        string source = shader.Source?.Text ?? string.Empty;
        source = ExpandIncludes(source, shader.Source?.FilePath);
        source = ShaderSnippets.ResolveSnippets(source);
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' does not contain GLSL source code.");

        AutoUniformRewriteResult rewrite = VulkanShaderAutoUniforms.Rewrite(source, shader.Type);
        rewrittenSource = rewrite.Source;
        autoUniformBlock = rewrite.BlockInfo;

        Compiler* compiler = ShadercApi.CompilerInitialize();
        if (compiler is null)
            throw new InvalidOperationException("Failed to initialize the shaderc compiler instance.");

        CompileOptions* options = ShadercApi.CompileOptionsInitialize();
        if (options is null)
        {
            ShadercApi.CompilerRelease(compiler);
            throw new InvalidOperationException("Failed to allocate shaderc compile options.");
        }

        ShadercApi.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
        ShadercApi.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);

        byte[] sourceBytes = Encoding.UTF8.GetBytes(rewrittenSource);
        byte[] nameBytes = GetNullTerminatedUtf8(shader.Name ?? $"Shader_{shader.GetHashCode():X8}");
        byte[] entryPointBytes = GetNullTerminatedUtf8(entryPoint);

        CompilationResult* result;
        fixed (byte* sourcePtr = sourceBytes)
        fixed (byte* namePtr = nameBytes)
        fixed (byte* entryPtr = entryPointBytes)
        {
            result = ShadercApi.CompileIntoSpv(
                compiler,
                sourcePtr,
                (nuint)sourceBytes.Length,
                ToShaderKind(shader.Type),
                namePtr,
                entryPtr,
                options);
        }

        try
        {
            if (result is null)
                throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' failed to compile due to an unknown error.");

            CompilationStatus status = ShadercApi.ResultGetCompilationStatus(result);
            if (status != CompilationStatus.Success)
            {
                string message = SilkMarshal.PtrToString((nint)ShadercApi.ResultGetErrorMessage(result)) ?? "Unknown error";
                bool includePreview = string.Equals(
                    Environment.GetEnvironmentVariable("XRE_VK_DUMP_SHADER_ON_ERROR"),
                    "1",
                    StringComparison.Ordinal);

                if (includePreview)
                {
                    string preview = BuildSourcePreview(rewrittenSource, 120);
                    throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' failed to compile: {message}{Environment.NewLine}{preview}");
                }

                throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' failed to compile: {message}");
            }

            nuint length = ShadercApi.ResultGetLength(result);
            if (length == 0)
                throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' produced an empty SPIR-V module.");

            byte[] spirv = new byte[(int)length];
            void* bytesPtr = ShadercApi.ResultGetBytes(result);
            Marshal.Copy((nint)bytesPtr, spirv, 0, spirv.Length);

            ShadercApi.ResultRelease(result);
            result = null;
            return spirv;
        }
        finally
        {
            if (result is not null)
                ShadercApi.ResultRelease(result);

            ShadercApi.CompileOptionsRelease(options);
            ShadercApi.CompilerRelease(compiler);
        }
    }

    private static byte[] GetNullTerminatedUtf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Array.Resize(ref bytes, bytes.Length + 1);
        bytes[^1] = 0;
        return bytes;
    }

    private static ShaderKind ToShaderKind(EShaderType type)
        => type switch
        {
            EShaderType.Vertex => ShaderKind.VertexShader,
            EShaderType.Fragment => ShaderKind.FragmentShader,
            EShaderType.Geometry => ShaderKind.GeometryShader,
            EShaderType.TessControl => ShaderKind.TessControlShader,
            EShaderType.TessEvaluation => ShaderKind.TessEvaluationShader,
            EShaderType.Compute => ShaderKind.ComputeShader,
            EShaderType.Task => ShaderKind.TaskShader,
            EShaderType.Mesh => ShaderKind.MeshShader,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

    private static string ExpandIncludes(string source, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(source) || !source.Contains("#include", StringComparison.Ordinal))
            return source;

        string? sourceDirectory = string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : Path.GetDirectoryName(sourcePath);

        return ExpandIncludesRecursive(source, sourceDirectory, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildSourcePreview(string source, int maxLines)
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int lineCount = Math.Min(lines.Length, Math.Max(1, maxLines));
        StringBuilder builder = new();
        builder.AppendLine("--- Rewritten GLSL preview ---");
        for (int i = 0; i < lineCount; i++)
            builder.AppendLine($"{i + 1,4}: {lines[i]}");

        return builder.ToString();
    }

    private static string ExpandIncludesRecursive(string source, string? currentDirectory, HashSet<string> includeStack)
    {
        StringBuilder output = new(source.Length + 128);
        using StringReader reader = new(source);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            Match includeMatch = IncludeRegex.Match(line);
            if (!includeMatch.Success)
            {
                output.AppendLine(line);
                continue;
            }

            string includePath = includeMatch.Groups["path"].Value.Trim();
            string resolvedPath = ResolveIncludePath(currentDirectory, includePath)
                ?? throw new InvalidOperationException($"Failed to resolve shader include '{includePath}'.");

            string normalizedPath = Path.GetFullPath(resolvedPath);
            if (!includeStack.Add(normalizedPath))
                throw new InvalidOperationException($"Recursive shader include detected for '{normalizedPath}'.");

            string includedSource = File.ReadAllText(normalizedPath);
            string expandedInclude = ExpandIncludesRecursive(includedSource, Path.GetDirectoryName(normalizedPath), includeStack);
            includeStack.Remove(normalizedPath);

            output.AppendLine($"// begin include {includePath}");
            output.AppendLine(expandedInclude);
            output.AppendLine($"// end include {includePath}");
        }

        return output.ToString();
    }

    private static string? ResolveIncludePath(string? currentDirectory, string includePath)
    {
        if (Path.IsPathRooted(includePath) && File.Exists(includePath))
            return includePath;

        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            string fromCurrentDirectory = Path.Combine(currentDirectory, includePath);
            if (File.Exists(fromCurrentDirectory))
                return fromCurrentDirectory;
        }

        string? fromShaderRoots = FindIncludeInShaderRoots(includePath);
        if (!string.IsNullOrWhiteSpace(fromShaderRoots))
            return fromShaderRoots;

        return null;
    }

    private static string? GetEngineShaderRoot()
        => string.IsNullOrWhiteSpace(Engine.Assets?.EngineAssetsPath)
            ? null
            : Path.Combine(Engine.Assets!.EngineAssetsPath, "Shaders");

    private static string? GetGameShaderRoot()
        => string.IsNullOrWhiteSpace(Engine.Assets?.GameAssetsPath)
            ? null
            : Path.Combine(Engine.Assets!.GameAssetsPath, "Shaders");

    private static string? FindIncludeInShaderRoots(string includePath)
    {
        if (string.IsNullOrWhiteSpace(includePath))
            return null;

        bool hasDirectory = includePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0;
        string?[] roots = [GetEngineShaderRoot(), GetGameShaderRoot()];

        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            if (hasDirectory)
            {
                string candidate = Path.Combine(root, includePath);
                if (File.Exists(candidate))
                    return candidate;
                continue;
            }

            try
            {
                foreach (string candidate in Directory.EnumerateFiles(root, includePath, SearchOption.AllDirectories))
                    return candidate;
            }
            catch
            {
                // Ignore IO errors and continue trying remaining roots.
            }
        }

        return null;
    }
}

internal static class VulkanShaderReflection
{
    private static readonly Regex LayoutRegex = new(
        @"layout\s*\((?<qualifiers>[^)]*)\)\s*(?:(?:readonly|writeonly|coherent|volatile|restrict)\s+)*(?<storage>uniform|buffer)\s+(?<declaration>[^;{]+)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ArrayRegex = new(@"\\[(?<size>\\d+)\\]", RegexOptions.Compiled);

    public static IReadOnlyList<DescriptorBindingInfo> ExtractBindings(byte[] spirv, ShaderStageFlags stage, string? glslSourceFallback = null)
    {
        if (spirv.Length > 0)
        {
            try
            {
                SpirvModule module = new(spirv, stage);
                var bindings = module.CollectDescriptorBindings();
                if (bindings.Count > 0)
                    return bindings;
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"SPIR-V reflection failed ({ex.Message}). Falling back to GLSL source parsing.");
            }
        }

        return ExtractBindingsFromSource(glslSourceFallback, stage);
    }

    private static IReadOnlyList<DescriptorBindingInfo> ExtractBindingsFromSource(string? source, ShaderStageFlags stage)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Array.Empty<DescriptorBindingInfo>();

        List<DescriptorBindingInfo> bindings = new();
        foreach (Match match in LayoutRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string qualifiers = match.Groups["qualifiers"].Value;
            string storage = match.Groups["storage"].Value;
            string declaration = match.Groups["declaration"].Value.Trim();

            if (!TryParseQualifier(qualifiers, "binding", out uint binding))
            {
                Debug.VulkanWarning($"Shader descriptor '{declaration}' is missing a binding index; skipping.");
                continue;
            }

            TryParseQualifier(qualifiers, "set", out uint set);

            DescriptorType descriptorType = ClassifyDescriptor(storage, declaration, source, match.Index + match.Length);
            uint arraySize = ExtractArraySize(declaration);
            string name = ExtractResourceName(declaration);

            bindings.Add(new DescriptorBindingInfo(set, binding, descriptorType, stage, arraySize == 0 ? 1u : arraySize, name));
        }

        return bindings;
    }

    private static bool TryParseQualifier(string qualifiers, string key, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(qualifiers))
            return false;

        string[] parts = qualifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            int equals = part.IndexOf('=');
            if (equals < 0)
                continue;

            string qualifierKey = part[..equals].Trim();
            if (!qualifierKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            string rawValue = part[(equals + 1)..].Trim();
            if (uint.TryParse(rawValue, out value))
                return true;
        }

        return false;
    }

    private static DescriptorType ClassifyDescriptor(string storage, string declaration, string source, int lookAheadIndex)
    {
        if (storage.Equals("buffer", StringComparison.OrdinalIgnoreCase))
            return DescriptorType.StorageBuffer;

        if (storage.Equals("uniform", StringComparison.OrdinalIgnoreCase))
        {
            if (DeclaresBlock(source, lookAheadIndex))
                return DescriptorType.UniformBuffer;

            if (declaration.Contains("sampler", StringComparison.OrdinalIgnoreCase))
                return DescriptorType.CombinedImageSampler;

            if (declaration.Contains("image", StringComparison.OrdinalIgnoreCase))
                return DescriptorType.StorageImage;

            return DescriptorType.UniformBuffer;
        }

        return DescriptorType.UniformBuffer;
    }

    private static bool DeclaresBlock(string source, int index)
    {
        for (int i = index; i < source.Length; i++)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c))
                continue;
            return c == '{';
        }
        return false;
    }

    private static uint ExtractArraySize(string declaration)
    {
        Match match = ArrayRegex.Match(declaration);
        return match.Success && uint.TryParse(match.Groups["size"].Value, out uint size) ? size : 0u;
    }

    private static string ExtractResourceName(string declaration)
    {
        string sanitized = declaration;
        int bracketIndex = sanitized.IndexOf('[');
        if (bracketIndex >= 0)
            sanitized = sanitized[..bracketIndex];

        string[] tokens = sanitized.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? string.Empty : tokens[^1];
    }

    private sealed class SpirvModule
    {
        private readonly uint[] _words;
        private readonly ShaderStageFlags _stage;
        private readonly Dictionary<uint, SpirvType> _types = new();
        private readonly Dictionary<uint, SpirvVariable> _variables = new();
        private readonly Dictionary<uint, SpirvDecorations> _decorations = new();
        private readonly Dictionary<uint, string> _names = new();
        private readonly Dictionary<uint, ulong> _constants = new();

        private const int HeaderWordCount = 5;

        public SpirvModule(byte[] spirv, ShaderStageFlags stage)
        {
            if (spirv.Length % sizeof(uint) != 0)
                throw new InvalidOperationException("SPIR-V bytecode length must be divisible by 4.");

            _words = MemoryMarshal.Cast<byte, uint>(spirv).ToArray();
            _stage = stage;
            Parse();
        }

        public List<DescriptorBindingInfo> CollectDescriptorBindings()
        {
            List<DescriptorBindingInfo> bindings = new();

            foreach (SpirvVariable variable in _variables.Values)
            {
                if (!_decorations.TryGetValue(variable.Id, out SpirvDecorations? decoration) || !decoration.HasBinding)
                    continue;

                if (!_types.TryGetValue(variable.TypeId, out SpirvType? pointer) || pointer.Kind != SpirvTypeKind.Pointer || pointer.ElementTypeId is null)
                    continue;

                uint elementTypeId = pointer.ElementTypeId.Value;
                uint descriptorCount = ResolveDescriptorCount(elementTypeId, out uint leafTypeId);
                DescriptorType descriptorType = ResolveDescriptorType(variable.StorageClass, leafTypeId);

                uint set = decoration.DescriptorSet ?? 0;
                uint binding = decoration.Binding ?? 0;
                string name = _names.TryGetValue(variable.Id, out string? foundName) ? foundName : string.Empty;

                bindings.Add(new DescriptorBindingInfo(set, binding, descriptorType, _stage, descriptorCount == 0 ? 1u : descriptorCount, name));
            }

            return bindings;
        }

        private void Parse()
        {
            if (_words.Length < HeaderWordCount)
                throw new InvalidOperationException("SPIR-V module header is incomplete.");

            int index = HeaderWordCount;
            while (index < _words.Length)
            {
                uint word = _words[index];
                int wordCount = (int)(word >> 16);
                ushort opCode = (ushort)(word & 0xFFFF);

                if (wordCount <= 0)
                    throw new InvalidOperationException($"Invalid SPIR-V word count for opcode {opCode}.");

                if (index + wordCount > _words.Length)
                    throw new InvalidOperationException("SPIR-V instruction extends beyond buffer.");

                ReadOnlySpan<uint> operands = new(_words, index + 1, wordCount - 1);
                switch ((SpirvOp)opCode)
                {
                    case SpirvOp.OpName:
                        ParseOpName(operands);
                        break;
                    case SpirvOp.OpDecorate:
                        ParseOpDecorate(operands);
                        break;
                    case SpirvOp.OpConstant:
                    case SpirvOp.OpSpecConstant:
                        ParseOpConstant(operands);
                        break;
                    case SpirvOp.OpVariable:
                        ParseOpVariable(operands);
                        break;
                    case SpirvOp.OpTypePointer:
                        ParseOpTypePointer(operands);
                        break;
                    case SpirvOp.OpTypeStruct:
                        ParseOpTypeStruct(operands);
                        break;
                    case SpirvOp.OpTypeArray:
                        ParseOpTypeArray(operands);
                        break;
                    case SpirvOp.OpTypeRuntimeArray:
                        ParseOpTypeRuntimeArray(operands);
                        break;
                    case SpirvOp.OpTypeImage:
                        ParseOpTypeImage(operands);
                        break;
                    case SpirvOp.OpTypeSampledImage:
                        ParseOpTypeSampledImage(operands);
                        break;
                    case SpirvOp.OpTypeSampler:
                        ParseOpTypeSampler(operands);
                        break;
                }

                index += wordCount;
            }
        }

        private void ParseOpName(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 1)
                return;

            uint targetId = operands[0];
            string name = DecodeString(operands.Slice(1));
            if (!string.IsNullOrEmpty(name))
                _names[targetId] = name;
        }

        private void ParseOpDecorate(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 2)
                return;

            uint targetId = operands[0];
            SpirvDecoration decoration = (SpirvDecoration)operands[1];
            SpirvDecorations info = GetOrCreateDecoration(targetId);

            switch (decoration)
            {
                case SpirvDecoration.DescriptorSet:
                    if (operands.Length >= 3)
                        info.DescriptorSet = operands[2];
                    break;
                case SpirvDecoration.Binding:
                    if (operands.Length >= 3)
                        info.Binding = operands[2];
                    break;
                case SpirvDecoration.Block:
                    info.Block = true;
                    break;
                case SpirvDecoration.BufferBlock:
                    info.BufferBlock = true;
                    break;
            }
        }

        private void ParseOpConstant(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 2)
                return;

            uint resultId = operands[1];
            ulong value = 0;
            for (int i = 2; i < operands.Length; i++)
                value |= (ulong)operands[i] << ((i - 2) * 32);

            _constants[resultId] = value;
        }

        private void ParseOpVariable(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 3)
                return;

            uint resultTypeId = operands[0];
            uint resultId = operands[1];
            SpirvStorageClass storageClass = (SpirvStorageClass)operands[2];

            _variables[resultId] = new SpirvVariable(resultId, resultTypeId, storageClass);
        }

        private void ParseOpTypePointer(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 3)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.Pointer,
                StorageClass = (SpirvStorageClass)operands[1],
                ElementTypeId = operands[2],
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeStruct(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 1)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.Struct,
                Members = operands.Length > 1 ? operands.Slice(1).ToArray() : []
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeArray(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 3)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.Array,
                ElementTypeId = operands[1],
                LengthId = operands[2],
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeRuntimeArray(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 2)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.RuntimeArray,
                ElementTypeId = operands[1],
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeImage(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 8)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.Image,
                ImageType = new SpirvImageInfo
                {
                    SampledTypeId = operands[1],
                    Dim = (SpirvDim)operands[2],
                    Depth = operands[3],
                    Arrayed = operands[4],
                    Multisampled = operands[5],
                    Sampled = operands[6],
                    ImageFormat = operands[7],
                    AccessQualifier = operands.Length >= 9 ? operands[8] : 0,
                }
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeSampledImage(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 2)
                return;

            uint resultId = operands[0];
            SpirvType type = new(resultId)
            {
                Kind = SpirvTypeKind.SampledImage,
                ElementTypeId = operands[1],
            };

            _types[resultId] = type;
        }

        private void ParseOpTypeSampler(ReadOnlySpan<uint> operands)
        {
            if (operands.Length < 1)
                return;

            uint resultId = operands[0];
            _types[resultId] = new SpirvType(resultId) { Kind = SpirvTypeKind.Sampler };
        }

        private uint ResolveDescriptorCount(uint typeId, out uint leafTypeId)
        {
            uint count = 1;
            uint current = typeId;

            while (_types.TryGetValue(current, out SpirvType? type))
            {
                if (type.Kind == SpirvTypeKind.Array)
                {
                    if (type.LengthId.HasValue && _constants.TryGetValue(type.LengthId.Value, out ulong length) && length > 0)
                        count *= (uint)Math.Max(1ul, length);

                    current = type.ElementTypeId ?? 0;
                }
                else if (type.Kind == SpirvTypeKind.RuntimeArray)
                {
                    current = type.ElementTypeId ?? 0;
                    break;
                }
                else
                {
                    break;
                }
            }

            leafTypeId = current;
            return count;
        }

        private DescriptorType ResolveDescriptorType(SpirvStorageClass storageClass, uint typeId)
        {
            if (!_types.TryGetValue(typeId, out SpirvType? type))
                return DescriptorType.UniformBuffer;

            switch (storageClass)
            {
                case SpirvStorageClass.UniformConstant:
                    return ResolveUniformConstantType(type);
                case SpirvStorageClass.Uniform:
                    return IsBufferBlock(typeId) ? DescriptorType.StorageBuffer : DescriptorType.UniformBuffer;
                case SpirvStorageClass.StorageBuffer:
                    return DescriptorType.StorageBuffer;
                default:
                    return DescriptorType.UniformBuffer;
            }
        }

        private DescriptorType ResolveUniformConstantType(SpirvType type)
        {
            switch (type.Kind)
            {
                case SpirvTypeKind.SampledImage:
                    return DescriptorType.CombinedImageSampler;
                case SpirvTypeKind.Sampler:
                    return DescriptorType.Sampler;
                case SpirvTypeKind.Image:
                    return ResolveImageDescriptor(type.ImageType);
                default:
                    return DescriptorType.UniformBuffer;
            }
        }

        private DescriptorType ResolveImageDescriptor(SpirvImageInfo? info)
        {
            if (info is null)
                return DescriptorType.UniformBuffer;

            if (info.Dim == SpirvDim.SubpassData)
                return DescriptorType.InputAttachment;

            bool storage = info.Sampled == 2;
            if (info.Dim == SpirvDim.Buffer)
                return storage ? DescriptorType.StorageTexelBuffer : DescriptorType.UniformTexelBuffer;

            return storage ? DescriptorType.StorageImage : DescriptorType.SampledImage;
        }

        private bool IsBufferBlock(uint typeId)
        {
            return _decorations.TryGetValue(typeId, out SpirvDecorations? decorations) && decorations.BufferBlock;
        }

        private SpirvDecorations GetOrCreateDecoration(uint id)
        {
            if (!_decorations.TryGetValue(id, out SpirvDecorations? decor))
            {
                decor = new SpirvDecorations();
                _decorations[id] = decor;
            }

            return decor;
        }

        private static string DecodeString(ReadOnlySpan<uint> words)
        {
            if (words.Length == 0)
                return string.Empty;

            ReadOnlySpan<byte> bytes = MemoryMarshal.Cast<uint, byte>(words);
            int nullIndex = bytes.IndexOf((byte)0);
            int length = nullIndex >= 0 ? nullIndex : bytes.Length;
            return length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes[..length]);
        }
    }

    private enum SpirvOp : ushort
    {
        OpName = 5,
        OpMemberName = 6,
        OpTypeInt = 21,
        OpTypeFloat = 22,
        OpTypeVector = 23,
        OpTypeMatrix = 24,
        OpTypeImage = 25,
        OpTypeSampler = 26,
        OpTypeSampledImage = 27,
        OpTypeArray = 28,
        OpTypeRuntimeArray = 29,
        OpTypeStruct = 30,
        OpTypePointer = 32,
        OpVariable = 59,
        OpConstant = 43,
        OpSpecConstant = 45,
        OpDecorate = 71,
    }

    private enum SpirvStorageClass : uint
    {
        UniformConstant = 0,
        Input = 1,
        Uniform = 2,
        Output = 3,
        Workgroup = 4,
        CrossWorkgroup = 5,
        Private = 6,
        Function = 7,
        Generic = 8,
        PushConstant = 9,
        AtomicCounter = 10,
        Image = 11,
        StorageBuffer = 12,
    }

    private enum SpirvDecoration : uint
    {
        Block = 2,
        BufferBlock = 3,
        Binding = 33,
        DescriptorSet = 34,
    }

    private enum SpirvDim : uint
    {
        Dim1D = 0,
        Dim2D = 1,
        Dim3D = 2,
        Cube = 3,
        Rect = 4,
        Buffer = 5,
        SubpassData = 6,
        Dim1DArray = 7,
        Dim2DArray = 8,
        CubeArray = 9,
    }

    private enum SpirvTypeKind
    {
        Unknown,
        Pointer,
        Struct,
        Array,
        RuntimeArray,
        Image,
        SampledImage,
        Sampler,
    }

    private sealed record SpirvType(uint Id)
    {
        public SpirvTypeKind Kind { get; init; } = SpirvTypeKind.Unknown;
        public SpirvStorageClass? StorageClass { get; init; }
        public uint? ElementTypeId { get; init; }
        public uint? LengthId { get; init; }
        public uint[] Members { get; init; } = [];
        public SpirvImageInfo? ImageType { get; init; }
    }

    private sealed record SpirvVariable(uint Id, uint TypeId, SpirvStorageClass StorageClass);

    private sealed class SpirvDecorations
    {
        public uint? DescriptorSet { get; set; }
        public uint? Binding { get; set; }
        public bool Block { get; set; }
        public bool BufferBlock { get; set; }
        public bool HasBinding => Binding.HasValue;
    }

    private sealed class SpirvImageInfo
    {
        public uint SampledTypeId { get; init; }
        public SpirvDim Dim { get; init; }
        public uint Depth { get; init; }
        public uint Arrayed { get; init; }
        public uint Multisampled { get; init; }
        public uint Sampled { get; init; }
        public uint ImageFormat { get; init; }
        public uint AccessQualifier { get; init; }
    }
}
