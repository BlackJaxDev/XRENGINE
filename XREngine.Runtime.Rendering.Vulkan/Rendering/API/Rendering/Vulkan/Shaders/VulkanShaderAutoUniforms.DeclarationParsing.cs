using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

internal static partial class VulkanShaderAutoUniforms
{
    private static int FindFirstFunctionDefinitionIndex(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return -1;

        Match match = FunctionDefinitionRegex.Match(source);
        return match.Success ? match.Index : -1;
    }

    private static int FindAutoUniformInsertionIndex(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return -1;

        int index = 0;
        while (index < source.Length)
        {
            int lineEnd = source.IndexOf('\n', index);
            int nextIndex = lineEnd < 0 ? source.Length : lineEnd + 1;
            int contentEnd = lineEnd < 0 ? source.Length : lineEnd;
            if (contentEnd > index && source[contentEnd - 1] == '\r')
                contentEnd--;

            string line = source[index..contentEnd];
            string trimmed = line.TrimStart();

            if (trimmed.Length == 0 ||
                trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("#version", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("#extension", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("#define", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("#line", StringComparison.OrdinalIgnoreCase))
            {
                index = nextIndex;
                continue;
            }

            break;
        }

        return index;
    }

    private static List<string> MoveRequiredStructDeclarationsBeforeInsertion(
        ref string source,
        IReadOnlyList<AutoUniformMember> members,
        int insertionIndex)
    {
        List<string> moved = [];
        if (string.IsNullOrWhiteSpace(source))
            return moved;

        int threshold = insertionIndex < 0 ? source.Length : insertionIndex;
        HashSet<string> requiredStructTypes = new(StringComparer.Ordinal);

        foreach (AutoUniformMember member in members)
        {
            if (string.IsNullOrWhiteSpace(member.GlslType) || GlslTypeMap.ContainsKey(member.GlslType))
                continue;

            requiredStructTypes.Add(member.GlslType);
        }

        if (requiredStructTypes.Count == 0)
            return moved;

        Dictionary<string, Match> declarationsByName = new(StringComparer.Ordinal);
        foreach (Match declaration in StructDeclarationRegex.Matches(source))
        {
            if (!declaration.Success)
                continue;

            Match nameMatch = StructNameRegex.Match(declaration.Value);
            if (!nameMatch.Success)
                continue;

            string structName = nameMatch.Groups["name"].Value;
            if (!declarationsByName.ContainsKey(structName))
                declarationsByName[structName] = declaration;
        }

        List<Match> declarationsToMove = [];
        HashSet<string> visitedStructTypes = new(StringComparer.Ordinal);
        Queue<string> pendingStructTypes = new(requiredStructTypes);

        while (pendingStructTypes.Count > 0)
        {
            string structType = pendingStructTypes.Dequeue();
            if (!visitedStructTypes.Add(structType))
                continue;

            if (!declarationsByName.TryGetValue(structType, out Match? declaration) || declaration is null)
                continue;

            foreach (Match fieldMatch in StructFieldTypeRegex.Matches(declaration.Value))
            {
                if (!fieldMatch.Success)
                    continue;

                string fieldType = fieldMatch.Groups["type"].Value;
                if (string.IsNullOrWhiteSpace(fieldType)
                    || GlslTypeMap.ContainsKey(fieldType)
                    || string.Equals(fieldType, structType, StringComparison.Ordinal))
                    continue;

                pendingStructTypes.Enqueue(fieldType);
            }

            if (declaration.Index >= threshold)
                declarationsToMove.Add(declaration);
        }

        if (declarationsToMove.Count == 0)
            return moved;

        // Find any const int/uint constants referenced as array bounds inside the structs
        // being moved.  If those constants also appear after the insertion threshold (e.g.
        // because a #pragma snippet pushed them late), move them too so the struct definition
        // doesn't reference an undeclared identifier.
        HashSet<string> neededConstantNames = new(StringComparer.Ordinal);
        foreach (Match structDecl in declarationsToMove)
        {
            foreach (Match arrayBound in ArrayRegex.Matches(structDecl.Value))
            {
                string sizeToken = arrayBound.Groups["size"].Value;
                if (!uint.TryParse(sizeToken, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                    neededConstantNames.Add(sizeToken);
            }
        }

        List<Match> constantsToMove = [];
        if (neededConstantNames.Count > 0)
        {
            foreach (Match constMatch in ConstIntegralRegex.Matches(source))
            {
                if (!constMatch.Success) continue;
                string name = constMatch.Groups["name"].Value;
                if (neededConstantNames.Contains(name) && constMatch.Index >= threshold)
                    constantsToMove.Add(constMatch);
            }
            foreach (Match defineMatch in DefineIntegralRegex.Matches(source))
            {
                if (!defineMatch.Success) continue;
                string name = defineMatch.Groups["name"].Value;
                if (neededConstantNames.Contains(name) && defineMatch.Index >= threshold)
                    constantsToMove.Add(defineMatch);
            }
        }

        // Remove all items from the source in reverse index order to preserve positions.
        var allToMove = declarationsToMove
            .Concat(constantsToMove)
            .OrderByDescending(m => m.Index)
            .ToList();

        StringBuilder updated = new(source);
        foreach (Match m in allToMove)
            updated.Remove(m.Index, m.Length);

        // Return declarations in forward source order so constants come before
        // the structs that depend on them.
        foreach (Match m in declarationsToMove.Concat(constantsToMove).OrderBy(m => m.Index))
            moved.Add(m.Value.Trim());

        source = updated.ToString();
        return moved;
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

    private static Dictionary<string, GlslStructDefinition> ParseStructDefinitions(string source, IReadOnlyDictionary<string, uint> integralConstants)
    {
        Dictionary<string, GlslStructDefinition> definitions = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(source))
            return definitions;

        foreach (Match declaration in StructDeclarationRegex.Matches(source))
        {
            if (!declaration.Success)
                continue;

            Match nameMatch = StructNameRegex.Match(declaration.Value);
            if (!nameMatch.Success)
                continue;

            string structName = nameMatch.Groups["name"].Value;
            int openBrace = declaration.Value.IndexOf('{');
            int closeBrace = declaration.Value.LastIndexOf('}');
            if (openBrace < 0 || closeBrace <= openBrace)
                continue;

            string body = declaration.Value[(openBrace + 1)..closeBrace];
            List<GlslStructField> fields = [];
            foreach (Match fieldMatch in StructFieldDeclarationRegex.Matches(body))
            {
                if (!fieldMatch.Success)
                    continue;

                string glslType = fieldMatch.Groups["type"].Value;
                string declarators = fieldMatch.Groups["declarators"].Value;
                foreach (string declarator in SplitDeclarators(declarators))
                {
                    if (!TryParseDeclarator(declarator, integralConstants, out string fieldName, out bool isArray, out uint arrayLength, out _))
                        continue;

                    fields.Add(new GlslStructField(glslType, fieldName, isArray, arrayLength));
                }
            }

            if (fields.Count > 0)
                definitions[structName] = new GlslStructDefinition(structName, fields);
        }

        return definitions;
    }

    /// <summary>
    /// Matches top-level bare <c>in</c> / <c>out</c> declarations that lack a
    /// <c>layout(location = …)</c> qualifier and are NOT built-in interface blocks
    /// (e.g. <c>gl_PerVertex</c>), <c>gl_*</c> variables, or interface-block openers.
    /// Group "dir" = in|out, Group "rest" = everything after the direction keyword up to and including the semicolon.
    /// </summary>
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

    internal static IReadOnlyList<string> FindOpaqueLikeTypesMissingClassification(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Array.Empty<string>();

        List<string> types = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in UniformStatementRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            if (!TryExtractTypeAndDeclarators(match.Groups["statement"].Value, out string glslType, out _))
                continue;

            if (IsOpaque(glslType) || !LooksLikeOpaqueType(glslType))
                continue;

            if (seen.Add(glslType))
                types.Add(glslType);
        }

        types.Sort(StringComparer.OrdinalIgnoreCase);
        return types;
    }

    private static bool LooksLikeOpaqueType(string glslType)
        => glslType.StartsWith("sampler", StringComparison.OrdinalIgnoreCase) ||
           glslType.StartsWith("image", StringComparison.OrdinalIgnoreCase) ||
           glslType.StartsWith("iimage", StringComparison.OrdinalIgnoreCase) ||
           glslType.StartsWith("uimage", StringComparison.OrdinalIgnoreCase) ||
           glslType.StartsWith("subpassInput", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(glslType, "atomic_uint", StringComparison.OrdinalIgnoreCase);
}
