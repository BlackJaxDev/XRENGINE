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
    private static readonly Regex FloatSuffixRegex = new(
        @"(?<![A-Za-z0-9_])(?<num>(?:\d+\.\d*|\d*\.\d+|\d+)(?:[eE][+-]?\d+)?)[fF](?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ShaderMainFunctionRegex = new(
        @"(?m)\bvoid\s+main\s*\(\s*(?:void\s*)?\)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GeometryEmitVertexRegex = new(
        @"(?m)^(?<indent>\s*)EmitVertex\s*\(\s*\)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MeshPositionAssignmentRegex = new(
        @"(?<target>gl_MeshVertices(?:EXT|NV)\s*\[[^\]]+\]\s*\.\s*gl_Position\s*)=\s*(?<expr>[^;]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UniformStatementRegex = new(
        @"^\s*(?:layout\s*\([^)]*\)\s*)?uniform\s+(?<statement>[^;]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex ArrayRegex = new(@"\[(?<size>[A-Za-z_][A-Za-z0-9_]*|\d+u?)\]", RegexOptions.Compiled);
    private static readonly Regex ConstIntegralRegex = new(
        @"\bconst\s+(?:uint|int)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>[^;]+?)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex DefineIntegralRegex = new(
        @"^\s*#\s*define\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+(?<value>[^\r\n]+?)\s*$",
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

    private static readonly Regex StructNameRegex = new(
        @"\bstruct\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StructFieldTypeRegex = new(
        @"(?m)^\s*(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+[A-Za-z_][A-Za-z0-9_]*(?:\s*\[[^\]]+\])?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StructFieldDeclarationRegex = new(
        @"(?m)^\s*(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<declarators>[^;{}]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FunctionDefinitionRegex = new(
        @"(?m)^\s*[A-Za-z_][A-Za-z0-9_\s]*\s+[A-Za-z_][A-Za-z0-9_]*\s*\([^;{}]*\)\s*\{",
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
        => Rewrite(source, shaderType, RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap);

    public static AutoUniformRewriteResult Rewrite(string source, EShaderType shaderType, bool useVulkanClipDepthRemap)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new AutoUniformRewriteResult(source, null);

        source = ApplyVulkanSourceFixups(source, shaderType, useVulkanClipDepthRemap);

        bool enableAutoUniformRewrite = XREngine.Rendering.RenderDiagnosticsFlags.VkEnableAutoUniformRewrite;

        if (!enableAutoUniformRewrite)
        {
            string rewrittenEarly = RewriteOpaqueUniformBindings(source, shaderType);
            rewrittenEarly = HoistOpaqueUniforms(rewrittenEarly);
            return new AutoUniformRewriteResult(rewrittenEarly, null);
        }

        Dictionary<string, uint> integralConstants = ParseIntegralConstants(source);
        Dictionary<string, GlslStructDefinition> structDefinitions = ParseStructDefinitions(source, integralConstants);

        List<(string GlslType, string Name, bool IsArray, uint ArrayLength, AutoUniformDefaultValue? DefaultValue, IReadOnlyList<AutoUniformDefaultValue>? DefaultArrayValues)> members = new();
        HashSet<string> memberNames = new(StringComparer.Ordinal);
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
                foreach (var member in statementMembers)
                {
                    if (memberNames.Add(member.Name))
                        members.Add(member);
                }
            }

            if (!canRewriteStatement)
                continue;

            output.Append(source, lastIndex, match.Index - lastIndex);
            lastIndex = match.Index + match.Length;
        }

        output.Append(source, lastIndex, source.Length - lastIndex);
        string rewritten = output.ToString();
        rewritten = RewriteOpaqueUniformBindings(rewritten, shaderType);
        rewritten = HoistOpaqueUniforms(rewritten);

        if (members.Count == 0)
            return new AutoUniformRewriteResult(rewritten, null);

        string blockName = GetAutoUniformBlockName(shaderType);
        string instanceName = $"{blockName}_Instance";

        if (!TryComputeBlockLayout(members, structDefinitions, out var layoutMembers, out uint blockSize))
            return new AutoUniformRewriteResult(source, null);

        uint binding = FindAvailableAutoUniformBinding(rewritten, shaderType);

        foreach (var member in layoutMembers)
        {
            rewritten = Regex.Replace(
                rewritten,
                $@"(?<!\.)\b{Regex.Escape(member.Name)}\b",
                $"{instanceName}.{member.Name}");
        }

        int insertionIndex = FindAutoUniformInsertionIndex(rewritten);
        List<string> movedStructDeclarations = MoveRequiredStructDeclarationsBeforeInsertion(ref rewritten, layoutMembers, insertionIndex);
        insertionIndex = FindAutoUniformInsertionIndex(rewritten);

        string block = BuildUniformBlock(blockName, instanceName, binding, layoutMembers);
        string insertionContent = movedStructDeclarations.Count == 0
            ? block
            : string.Join(Environment.NewLine + Environment.NewLine, movedStructDeclarations) + Environment.NewLine + Environment.NewLine + block;

        rewritten = InsertAtPreferredLocation(rewritten, insertionContent, insertionIndex);

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
    /// <c>layout(location = â€¦)</c> qualifier and are NOT built-in interface blocks
    /// (e.g. <c>gl_PerVertex</c>), <c>gl_*</c> variables, or interface-block openers.
    /// Group "dir" = in|out, Group "rest" = everything after the direction keyword up to and including the semicolon.
    /// </summary>
    private static readonly Regex BareIODeclarationRegex = new(
        @"^(?<indent>\s*)(?<dir>in|out)\s+(?<rest>(?!gl_)[A-Za-z_]\w*(?:\s+[A-Za-z_]\w*(?:\s*\[[^\]]*\])?)*\s*;)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Finds the highest <c>location = N</c> value already declared in the source for a given direction.
    /// </summary>
    private static uint FindMaxIOLocation(string source, string direction)
    {
        Regex locationRegex = new(
            $@"layout\s*\([^)]*location\s*=\s*(?<loc>\d+)[^)]*\)\s*{direction}\b",
            RegexOptions.IgnoreCase);

        uint max = 0;
        bool found = false;
        foreach (Match m in locationRegex.Matches(source))
        {
            if (uint.TryParse(m.Groups["loc"].Value, out uint loc))
            {
                found = true;
                if (loc >= max) max = loc + 1;
            }
        }
        return found ? max : 0;
    }

    private static string EnsureIOLocationQualifiers(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return source;

        // Pre-scan to find function bodies so we can skip bare in/out inside them.
        HashSet<int> functionBodyLines = new();
        string[] allLines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int braceDepth = 0;
        for (int i = 0; i < allLines.Length; i++)
        {
            foreach (char ch in allLines[i])
            {
                if (ch == '{') braceDepth++;
                else if (ch == '}') braceDepth = Math.Max(0, braceDepth - 1);
            }
            if (braceDepth > 0)
                functionBodyLines.Add(i);
        }

        uint nextInLocation = FindMaxIOLocation(source, "in");
        uint nextOutLocation = FindMaxIOLocation(source, "out");

        // Track current line number for each match to skip function bodies.
        string result = BareIODeclarationRegex.Replace(source, match =>
        {
            // Compute line number of the match.
            int lineIndex = source[..match.Index].Split('\n').Length - 1;
            if (functionBodyLines.Contains(lineIndex))
                return match.Value;

            string rest = match.Groups["rest"].Value;

            // Skip interface blocks (type name followed by '{' after semicolon â€” but these would have { not ; so regex won't match).
            // Skip gl_ builtins that somehow sneak through.
            if (rest.StartsWith("gl_", StringComparison.Ordinal))
                return match.Value;

            string dir = match.Groups["dir"].Value;
            string indent = match.Groups["indent"].Value;
            uint loc = string.Equals(dir, "out", StringComparison.Ordinal) ? nextOutLocation++ : nextInLocation++;
            return $"{indent}layout(location = {loc}) {dir} {rest}";
        });

        return result;
    }

    /// <summary>
    /// Moves opaque uniform declarations (samplers, images, etc.) that appear after
    /// the first function definition to just before it. glslang (Vulkan/SPIR-V) requires
    /// declarations to appear before their usage, unlike typical OpenGL GLSL compilers
    /// which resolve global-scope symbols regardless of declaration order.
    /// </summary>
    /// <summary>
    /// Returns the character ranges (start inclusive, end exclusive) of all text inside
    /// preprocessor conditional blocks (#ifdef / #ifndef / #if â€¦ #endif).
    /// Used by HoistOpaqueUniforms to avoid pulling uniforms out of their conditional context.
    /// </summary>
    private static List<(int Start, int End)> GetPreprocessorConditionalRanges(string source)
    {
        var ranges = new List<(int Start, int End)>();
        var openStack = new Stack<int>();

        int lineStart = 0;
        int len = source.Length;
        while (lineStart < len)
        {
            int lineEnd = source.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = len;

            // Get line text without the newline (and strip optional \r).
            int contentEnd = lineEnd > lineStart && source[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            string trimmed = source.Substring(lineStart, contentEnd - lineStart).TrimStart();

            if (trimmed.StartsWith("#ifdef", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("#ifndef", StringComparison.OrdinalIgnoreCase) ||
                (trimmed.StartsWith("#if", StringComparison.OrdinalIgnoreCase) &&
                 trimmed.Length > 3 && (trimmed[3] == ' ' || trimmed[3] == '\t')))
            {
                openStack.Push(lineStart);
            }
            else if (trimmed.StartsWith("#endif", StringComparison.OrdinalIgnoreCase) && openStack.Count > 0)
            {
                int start = openStack.Pop();
                int end = lineEnd < len ? lineEnd + 1 : len; // include the newline after #endif
                ranges.Add((start, end));
            }

            lineStart = lineEnd < len ? lineEnd + 1 : len;
        }

        // Any unclosed blocks extend to the end of the source.
        while (openStack.Count > 0)
            ranges.Add((openStack.Pop(), len));

        return ranges;
    }

    private static bool IsInsideConditionalRange(int charIndex, List<(int Start, int End)> ranges)
    {
        foreach (var (start, end) in ranges)
            if (charIndex >= start && charIndex < end)
                return true;
        return false;
    }

    private static string HoistOpaqueUniforms(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return source;

        int firstFuncIndex = FindFirstFunctionDefinitionIndex(source);
        if (firstFuncIndex < 0)
            return source;

        // Precompute ranges of text inside preprocessor conditionals so we don't
        // hoist opaque uniforms out of their #ifdef / #else context.
        var conditionalRanges = GetPreprocessorConditionalRanges(source);

        var toHoist = new List<(int Start, int Length, string Line)>();
        HashSet<string> neededConstantNames = new(StringComparer.Ordinal);
        foreach (Match match in OpaqueUniformRegex.Matches(source))
        {
            if (match.Index < firstFuncIndex)
                continue;

            string glslType = match.Groups["type"].Value;
            if (!IsOpaque(glslType))
                continue;

            // Skip uniforms that live inside a preprocessor conditional block.
            // Those declarations must remain in their branch context.
            if (IsInsideConditionalRange(match.Index, conditionalRanges))
                continue;

            string line = match.Value.Trim();
            toHoist.Add((match.Index, match.Length, line));
            CollectArrayBoundConstantNames(line, neededConstantNames);
        }

        if (toHoist.Count == 0)
            return source;

        List<Match> constantsToHoist = FindConstantDeclarationsToMove(source, neededConstantNames, firstFuncIndex);

        // Remove declarations from original positions (reverse order preserves indices).
        var sb = new StringBuilder(source);
        var removals = new List<(int Start, int Length)>(constantsToHoist.Count + toHoist.Count);
        removals.AddRange(constantsToHoist.Select(static constant => (constant.Index, constant.Length)));
        removals.AddRange(toHoist.Select(static uniform => (uniform.Start, uniform.Length)));
        foreach (var (start, length) in removals.OrderByDescending(static removal => removal.Start))
            RemoveMatchAndTrailingNewline(sb, start, length);

        // Build the hoisted block.
        var hoisted = new StringBuilder();
        foreach (Match constant in constantsToHoist.OrderBy(static match => match.Index))
            hoisted.AppendLine(constant.Value.Trim());
        foreach (var (_, _, line) in toHoist)
            hoisted.AppendLine(line);

        // Insert in the top-level preamble so declarations do not land inside
        // a conditional function block that preprocessing may remove.
        string modified = sb.ToString();
        int insertPos = FindAutoUniformInsertionIndex(modified);
        if (insertPos < 0)
            insertPos = modified.Length;

        return InsertAtPreferredLocation(modified, hoisted.ToString().TrimEnd(), insertPos);
    }

    private static void RemoveMatchAndTrailingNewline(StringBuilder source, int start, int length)
    {
        int end = start + length;
        if (end < source.Length && source[end] == '\r')
            end++;
        if (end < source.Length && source[end] == '\n')
            end++;

        source.Remove(start, end - start);
    }

    private static void CollectArrayBoundConstantNames(string declaration, HashSet<string> names)
    {
        foreach (Match arrayBound in ArrayRegex.Matches(declaration))
        {
            string sizeToken = arrayBound.Groups["size"].Value.Trim();
            sizeToken = sizeToken.TrimEnd('u', 'U');
            if (!uint.TryParse(sizeToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                names.Add(sizeToken);
        }
    }

    private static List<Match> FindConstantDeclarationsToMove(string source, HashSet<string> initialNames, int threshold)
    {
        List<Match> constantsToMove = [];
        if (initialNames.Count == 0)
            return constantsToMove;

        HashSet<string> pending = new(initialNames, StringComparer.Ordinal);
        HashSet<string> moved = new(StringComparer.Ordinal);

        bool movedAny;
        do
        {
            movedAny = false;

            foreach (Match constMatch in ConstIntegralRegex.Matches(source))
            {
                if (!TryQueueConstantDeclaration(constMatch, source, threshold, pending, moved, constantsToMove))
                    continue;

                string expression = constMatch.Groups["value"].Value;
                CollectIdentifierTokens(expression, pending, moved);
                movedAny = true;
            }

            foreach (Match defineMatch in DefineIntegralRegex.Matches(source))
            {
                if (!TryQueueConstantDeclaration(defineMatch, source, threshold, pending, moved, constantsToMove))
                    continue;

                string expression = defineMatch.Groups["value"].Value;
                CollectIdentifierTokens(expression, pending, moved);
                movedAny = true;
            }
        }
        while (movedAny);

        return constantsToMove;
    }

    private static bool TryQueueConstantDeclaration(
        Match match,
        string source,
        int threshold,
        HashSet<string> pending,
        HashSet<string> moved,
        List<Match> constantsToMove)
    {
        if (!match.Success || match.Index < threshold)
            return false;

        string name = match.Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(name) || !pending.Contains(name) || !moved.Add(name))
            return false;

        pending.Remove(name);
        constantsToMove.Add(match);
        return true;
    }

    private static void CollectIdentifierTokens(string expression, HashSet<string> pending, HashSet<string> moved)
    {
        foreach (Match identifier in Regex.Matches(expression, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
        {
            string name = identifier.Value;
            if (!moved.Contains(name))
                pending.Add(name);
        }
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
                    : EnsureLayoutHasSetAndBinding(existingLayout, VulkanRenderer.DescriptorSetMaterial, nextBinding++);
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

    private static string EnsureLayoutHasSetAndBinding(string layout, uint set, uint binding)
    {
        Match layoutMatch = LayoutQualifierRegex.Match(layout);
        if (!layoutMatch.Success)
            return $"layout(set = {set}, binding = {binding}) ";

        string qualifiers = layoutMatch.Groups["qualifiers"].Value.Trim();
        bool hasSet = Regex.IsMatch(qualifiers, @"\bset\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool hasBinding = Regex.IsMatch(qualifiers, @"\bbinding\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        List<string> parts = string.IsNullOrWhiteSpace(qualifiers)
            ? []
            : qualifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (!hasSet)
            parts.Add($"set = {set}");
        if (!hasBinding)
            parts.Add($"binding = {binding}");

        string updated = parts.Count == 0 ? string.Empty : string.Join(", ", parts);
        return LayoutQualifierRegex.Replace(layout, $"layout({updated}) ", 1);
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

    private const uint AutoUniformBindingBase = 64u;
    private const uint AutoUniformBindingWindowSize = 8u;

    private static uint FindAvailableAutoUniformBinding(string source, EShaderType shaderType)
    {
        uint binding = GetAutoUniformBindingBase(shaderType);
        uint bindingEnd = binding + AutoUniformBindingWindowSize;
        HashSet<uint> usedBindings = CollectLayoutBindings(source);

        while (binding < bindingEnd && usedBindings.Contains(binding))
            binding++;

        if (binding < bindingEnd)
            return binding;

        binding = Math.Max(FindNextBinding(source), bindingEnd);
        while (usedBindings.Contains(binding))
            binding++;

        return binding;
    }

    private static uint GetAutoUniformBindingBase(EShaderType shaderType)
    {
        uint stageSlot = shaderType switch
        {
            EShaderType.Fragment => 0u,
            EShaderType.Vertex => 1u,
            EShaderType.Geometry => 2u,
            EShaderType.TessControl => 3u,
            EShaderType.TessEvaluation => 4u,
            EShaderType.Compute => 5u,
            EShaderType.Task => 6u,
            EShaderType.Mesh => 7u,
            _ => 1u,
        };

        return AutoUniformBindingBase + (stageSlot * AutoUniformBindingWindowSize);
    }

    private static HashSet<uint> CollectLayoutBindings(string source)
    {
        HashSet<uint> used = [];
        foreach (Match match in LayoutQualifierRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string qualifiers = match.Groups["qualifiers"].Value;
            if (TryParseQualifier(qualifiers, "binding", out uint value))
                used.Add(value);
        }

        return used;
    }

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
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in ConstIntegralRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string name = match.Groups["name"].Value;
            string valueText = match.Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(valueText))
                continue;

            candidates[name] = valueText;
        }

        foreach (Match match in DefineIntegralRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string name = match.Groups["name"].Value;
            string valueText = match.Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(valueText))
                continue;

            candidates[name] = valueText;
        }

        for (int pass = 0; pass < candidates.Count; pass++)
        {
            bool resolvedAny = false;
            foreach ((string name, string expression) in candidates)
            {
                if (constants.ContainsKey(name))
                    continue;

                if (!TryEvaluateIntegralExpression(expression, constants, out uint value))
                    continue;

                constants[name] = value;
                resolvedAny = true;
            }

            if (!resolvedAny)
                break;
        }

        return constants;
    }

    private static bool TryEvaluateIntegralExpression(
        string expression,
        IReadOnlyDictionary<string, uint> constants,
        out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        int lineComment = expression.IndexOf("//", StringComparison.Ordinal);
        if (lineComment >= 0)
            expression = expression[..lineComment];

        IntegralExpressionParser parser = new(expression, constants);
        if (!parser.TryParse(out long parsed) || parsed < 0 || parsed > uint.MaxValue)
            return false;

        value = (uint)parsed;
        return true;
    }

    private sealed class IntegralExpressionParser
    {
        private readonly string _expression;
        private readonly IReadOnlyDictionary<string, uint> _constants;
        private int _index;

        public IntegralExpressionParser(string expression, IReadOnlyDictionary<string, uint> constants)
        {
            _expression = expression;
            _constants = constants;
        }

        public bool TryParse(out long value)
        {
            _index = 0;
            if (!TryParseAdditive(out value))
                return false;

            SkipWhitespace();
            return _index == _expression.Length;
        }

        private bool TryParseAdditive(out long value)
        {
            if (!TryParseMultiplicative(out value))
                return false;

            while (true)
            {
                SkipWhitespace();
                char op = Peek();
                if (op is not ('+' or '-'))
                    return true;

                _index++;

                if (!TryParseMultiplicative(out long rhs))
                    return false;

                value = op == '-' ? value - rhs : value + rhs;
            }
        }

        private bool TryParseMultiplicative(out long value)
        {
            if (!TryParseUnary(out value))
                return false;

            while (true)
            {
                SkipWhitespace();
                char op = Peek();
                if (op is not ('*' or '/' or '%'))
                    return true;

                _index++;
                if (!TryParseUnary(out long rhs))
                    return false;

                switch (op)
                {
                    case '*':
                        value *= rhs;
                        break;
                    case '/':
                        if (rhs == 0)
                            return false;
                        value /= rhs;
                        break;
                    case '%':
                        if (rhs == 0)
                            return false;
                        value %= rhs;
                        break;
                }
            }
        }

        private bool TryParseUnary(out long value)
        {
            SkipWhitespace();
            if (TryConsume('+'))
                return TryParseUnary(out value);

            if (TryConsume('-'))
            {
                if (!TryParseUnary(out value))
                    return false;

                value = -value;
                return true;
            }

            return TryParsePrimary(out value);
        }

        private bool TryParsePrimary(out long value)
        {
            SkipWhitespace();
            value = 0;

            if (TryConsume('('))
            {
                if (!TryParseAdditive(out value))
                    return false;

                SkipWhitespace();
                return TryConsume(')');
            }

            char current = Peek();
            if (char.IsDigit(current))
                return TryParseNumber(out value);

            if (current == '_' || char.IsLetter(current))
                return TryParseIdentifier(out value);

            return false;
        }

        private bool TryParseNumber(out long value)
        {
            int start = _index;
            while (_index < _expression.Length && char.IsDigit(_expression[_index]))
                _index++;

            if (_index < _expression.Length && (_expression[_index] == 'u' || _expression[_index] == 'U'))
                _index++;

            string token = _expression[start.._index].TrimEnd('u', 'U');
            return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private bool TryParseIdentifier(out long value)
        {
            int start = _index;
            _index++;
            while (_index < _expression.Length && (_expression[_index] == '_' || char.IsLetterOrDigit(_expression[_index])))
                _index++;

            string name = _expression[start.._index];
            if (!_constants.TryGetValue(name, out uint constant))
            {
                value = 0;
                return false;
            }

            value = constant;
            return true;
        }

        private char Peek()
            => _index < _expression.Length ? _expression[_index] : '\0';

        private bool TryConsume(char value)
        {
            SkipWhitespace();
            if (Peek() != value)
                return false;

            _index++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_index < _expression.Length && char.IsWhiteSpace(_expression[_index]))
                _index++;
        }
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
        IReadOnlyDictionary<string, GlslStructDefinition> structDefinitions,
        out List<AutoUniformMember> layoutMembers,
        out uint blockSize)
    {
        layoutMembers = new List<AutoUniformMember>(members.Count);
        blockSize = 0;
        uint offset = 0;

        foreach (var (GlslType, Name, IsArray, ArrayLength, DefaultValue, DefaultArrayValues) in members)
        {
            if (!TryGetStd140Info(
                    GlslType,
                    IsArray,
                    ArrayLength,
                    structDefinitions,
                    out uint alignment,
                    out uint size,
                    out uint arrayStride,
                    out EShaderVarType? engineType,
                    out IReadOnlyList<AutoUniformMember>? structMembers))
                return false;

            offset = Align(offset, alignment);
            layoutMembers.Add(new AutoUniformMember(Name, GlslType, engineType, IsArray, ArrayLength, arrayStride, offset, size, DefaultValue, DefaultArrayValues, structMembers));
            offset += size;
        }

        blockSize = Align(offset, 16);
        return true;
    }

    private static uint Align(uint value, uint alignment)
        => alignment == 0 ? value : (uint)((value + alignment - 1) / alignment * alignment);

    private static bool TryGetStd140Info(
        string glslType,
        bool isArray,
        uint arrayLength,
        IReadOnlyDictionary<string, GlslStructDefinition> structDefinitions,
        out uint alignment,
        out uint size,
        out uint arrayStride,
        out EShaderVarType? engineType,
        out IReadOnlyList<AutoUniformMember>? structMembers)
    {
        alignment = 0;
        size = 0;
        arrayStride = 0;
        engineType = GlslTypeMap.TryGetValue(glslType, out var mapped) ? mapped : null;
        structMembers = null;

        if (!TryGetStd140Base(glslType, structDefinitions, out uint baseAlignment, out uint baseSize, out structMembers))
            return false;

        if (!isArray)
        {
            alignment = baseAlignment;
            size = baseSize;
            return true;
        }

        if (arrayLength == 0)
            return false;

        uint stride = Align(baseSize, 16u);
        alignment = Math.Max(baseAlignment, 16u);
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

    private static bool TryGetStd140Base(
        string glslType,
        IReadOnlyDictionary<string, GlslStructDefinition> structDefinitions,
        out uint alignment,
        out uint size,
        out IReadOnlyList<AutoUniformMember>? structMembers)
    {
        alignment = 0;
        size = 0;
        structMembers = null;

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
            case "ivec3":
            case "uvec3":
            case "bvec3":
                alignment = 16;
                size = 12;
                return true;
            case "vec4":
            case "ivec4":
            case "uvec4":
            case "bvec4":
                alignment = 16;
                size = 16;
                return true;
            case "dvec3":
                alignment = 32;
                size = 24;
                return true;
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
                return TryGetStd140StructInfo(glslType, structDefinitions, out alignment, out size, out structMembers);
        }
    }

    private static bool TryGetStd140StructInfo(
        string glslType,
        IReadOnlyDictionary<string, GlslStructDefinition> structDefinitions,
        out uint alignment,
        out uint size,
        out IReadOnlyList<AutoUniformMember>? structMembers)
    {
        alignment = 0;
        size = 0;
        structMembers = null;

        if (!structDefinitions.TryGetValue(glslType, out GlslStructDefinition? definition) || definition is null)
            return false;

        List<AutoUniformMember> fields = new(definition.Fields.Count);
        uint offset = 0;
        uint maxAlignment = 0;

        foreach (GlslStructField field in definition.Fields)
        {
            if (!TryGetStd140Info(
                    field.GlslType,
                    field.IsArray,
                    field.ArrayLength,
                    structDefinitions,
                    out uint fieldAlignment,
                    out uint fieldSize,
                    out uint fieldArrayStride,
                    out EShaderVarType? fieldEngineType,
                    out IReadOnlyList<AutoUniformMember>? childStructMembers))
            {
                return false;
            }

            offset = Align(offset, fieldAlignment);
            fields.Add(new AutoUniformMember(
                field.Name,
                field.GlslType,
                fieldEngineType,
                field.IsArray,
                field.ArrayLength,
                fieldArrayStride,
                offset,
                fieldSize,
                null,
                null,
                childStructMembers));

            offset += fieldSize;
            maxAlignment = Math.Max(maxAlignment, fieldAlignment);
        }

        alignment = Math.Max(Align(maxAlignment, 16u), 16u);
        size = Align(offset, alignment);
        structMembers = fields;
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
