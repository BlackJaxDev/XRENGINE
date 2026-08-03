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
        @"^\s*(?:layout\s*\([^)]*\)\s*)?uniform\s+(?<statement>[^;]+);[ \t]*(?://[ \t]*XRENGINE_FREQUENCY[ \t]*\([ \t]*(?<frequency>[A-Za-z_][A-Za-z0-9_]*)[ \t]*\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex FrequencyOverrideRegex = new(
        @"^[ \t]*//[ \t]*XRENGINE_FREQUENCY_OVERRIDE[ \t]*\([ \t]*(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]*,[ \t]*(?<frequency>[A-Za-z_][A-Za-z0-9_]*)[ \t]*\)[ \t]*\r?$",
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
        => Rewrite(
            source,
            shaderType,
            useVulkanClipDepthRemap,
            explicitFrequencyHints: null);

    internal static AutoUniformRewriteResult Rewrite(
        string source,
        EShaderType shaderType,
        bool useVulkanClipDepthRemap,
        IReadOnlyDictionary<string, EVulkanBindingFrequency>? explicitFrequencyHints)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new AutoUniformRewriteResult(
                source,
                Array.Empty<AutoUniformBlockInfo>());

        source = ApplyVulkanSourceFixups(source, shaderType, useVulkanClipDepthRemap);

        bool enableAutoUniformRewrite = XREngine.Rendering.RenderDiagnosticsFlags.VkEnableAutoUniformRewrite;

        if (!enableAutoUniformRewrite)
        {
            string rewrittenEarly = RewriteOpaqueUniformBindings(source, shaderType);
            rewrittenEarly = HoistOpaqueUniforms(rewrittenEarly);
            return new AutoUniformRewriteResult(
                rewrittenEarly,
                Array.Empty<AutoUniformBlockInfo>());
        }

        Dictionary<string, uint> integralConstants = ParseIntegralConstants(source);
        Dictionary<string, GlslStructDefinition> structDefinitions = ParseStructDefinitions(source, integralConstants);
        Dictionary<string, EVulkanBindingFrequency> explicitFrequencyOverrides =
            ParseExplicitFrequencyOverrides(source);
        MergeExplicitFrequencyHints(
            explicitFrequencyOverrides,
            explicitFrequencyHints);

        List<(string GlslType, string Name, bool IsArray, uint ArrayLength, AutoUniformDefaultValue? DefaultValue, IReadOnlyList<AutoUniformDefaultValue>? DefaultArrayValues, EVulkanBindingFrequency? ExplicitFrequency)> members = new();
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
            var statementMembers = new List<(string GlslType, string Name, bool IsArray, uint ArrayLength, AutoUniformDefaultValue? DefaultValue, IReadOnlyList<AutoUniformDefaultValue>? DefaultArrayValues, EVulkanBindingFrequency? ExplicitFrequency)>();
            EVulkanBindingFrequency? statementFrequency =
                ParseExplicitFrequencyAnnotation(match);

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

                statementMembers.Add((
                    glslType,
                    name,
                    isArray,
                    arrayLength,
                    defaultValue,
                    defaultArrayValues,
                    ResolveExplicitFrequency(
                        name,
                        statementFrequency,
                        explicitFrequencyOverrides)));
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
            return new AutoUniformRewriteResult(
                rewritten,
                Array.Empty<AutoUniformBlockInfo>());

        int frequencyCount = (int)EVulkanBindingFrequency.Count;
        List<(string GlslType, string Name, bool IsArray, uint ArrayLength, AutoUniformDefaultValue? DefaultValue, IReadOnlyList<AutoUniformDefaultValue>? DefaultArrayValues)>[] membersByFrequency =
            new List<(string, string, bool, uint, AutoUniformDefaultValue?, IReadOnlyList<AutoUniformDefaultValue>?)>[frequencyCount];
        for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
        {
            var member = members[memberIndex];
            EVulkanBindingFrequency frequency =
                member.ExplicitFrequency ??
                VulkanAutoUniformBindingSchema.ResolveDeclaredFrequency(
                    member.Name);
            int frequencyIndex = (int)frequency;
            membersByFrequency[frequencyIndex] ??= [];
            membersByFrequency[frequencyIndex]!.Add((
                member.GlslType,
                member.Name,
                member.IsArray,
                member.ArrayLength,
                member.DefaultValue,
                member.DefaultArrayValues));
        }

        int populatedFrequencyCount = 0;
        for (EVulkanBindingFrequency frequency =
                EVulkanBindingFrequency.Frame;
             frequency < EVulkanBindingFrequency.Count;
             frequency++)
        {
            if (membersByFrequency[(int)frequency] is { Count: > 0 })
                populatedFrequencyCount++;
        }

        uint[] bindings = FindAvailableAutoUniformBindings(
            rewritten,
            shaderType,
            populatedFrequencyCount);
        List<AutoUniformBlockInfo> blockInfos =
            new(populatedFrequencyCount);
        List<AutoUniformMember> allLayoutMembers = new(members.Count);
        List<string> blocks = new(populatedFrequencyCount);
        string blockNamePrefix = GetAutoUniformBlockName(shaderType);
        int bindingIndex = 0;

        for (EVulkanBindingFrequency frequency =
                EVulkanBindingFrequency.Frame;
             frequency < EVulkanBindingFrequency.Count;
             frequency++)
        {
            List<(string GlslType, string Name, bool IsArray, uint ArrayLength, AutoUniformDefaultValue? DefaultValue, IReadOnlyList<AutoUniformDefaultValue>? DefaultArrayValues)>? frequencyMembers =
                membersByFrequency[(int)frequency];
            if (frequencyMembers is not { Count: > 0 })
                continue;

            if (!TryComputeBlockLayout(
                    frequencyMembers,
                    structDefinitions,
                    out List<AutoUniformMember> layoutMembers,
                    out uint blockSize))
            {
                return new AutoUniformRewriteResult(
                    source,
                    Array.Empty<AutoUniformBlockInfo>());
            }

            string frequencyName = frequency.ToString();
            string blockName = $"{blockNamePrefix}_{frequencyName}";
            string instanceName = $"{blockName}_Instance";
            uint binding = bindings[bindingIndex++];

            for (int memberIndex = 0;
                 memberIndex < layoutMembers.Count;
                 memberIndex++)
            {
                AutoUniformMember member = layoutMembers[memberIndex];
                rewritten = Regex.Replace(
                    rewritten,
                    $@"(?<!\.)\b{Regex.Escape(member.Name)}\b",
                    $"{instanceName}.{member.Name}");
            }

            allLayoutMembers.AddRange(layoutMembers);
            blocks.Add(
                BuildUniformBlock(
                    blockName,
                    instanceName,
                    binding,
                    layoutMembers));
            blockInfos.Add(
                new AutoUniformBlockInfo(
                    blockName,
                    instanceName,
                    VulkanRenderer.DescriptorSetGlobals,
                    binding,
                    blockSize,
                    layoutMembers,
                    shaderType,
                    frequency));
        }

        int insertionIndex = FindAutoUniformInsertionIndex(rewritten);
        List<string> movedStructDeclarations =
            MoveRequiredStructDeclarationsBeforeInsertion(
                ref rewritten,
                allLayoutMembers,
                insertionIndex);
        insertionIndex = FindAutoUniformInsertionIndex(rewritten);

        string blockContent = string.Join(
            Environment.NewLine,
            blocks);
        string insertionContent = movedStructDeclarations.Count == 0
            ? blockContent
            : string.Join(
                Environment.NewLine + Environment.NewLine,
                movedStructDeclarations) +
                Environment.NewLine +
                Environment.NewLine +
                blockContent;

        rewritten = InsertAtPreferredLocation(
            rewritten,
            insertionContent,
            insertionIndex);
        return new AutoUniformRewriteResult(rewritten, blockInfos);
    }

    /// <summary>
    /// Captures comment-based ownership metadata before source optimization can
    /// prune an adjacent declaration and its trailing annotation.
    /// </summary>
    internal static IReadOnlyDictionary<string, EVulkanBindingFrequency> CaptureExplicitFrequencyHints(
        string source)
    {
        Dictionary<string, EVulkanBindingFrequency> hints =
            ParseExplicitFrequencyOverrides(source);
        Dictionary<string, uint> integralConstants = ParseIntegralConstants(source);
        foreach (Match match in UniformStatementRegex.Matches(source))
        {
            EVulkanBindingFrequency? frequency =
                ParseExplicitFrequencyAnnotation(match);
            if (!frequency.HasValue)
                continue;

            string statement = match.Groups["statement"].Value;
            if (statement.IndexOf('{') >= 0 ||
                !TryExtractTypeAndDeclarators(
                    statement,
                    out _,
                    out string declarators))
            {
                continue;
            }

            foreach (string declarator in SplitDeclarators(declarators))
            {
                if (!TryParseDeclarator(
                        declarator,
                        integralConstants,
                        out string name,
                        out _,
                        out _,
                        out _))
                {
                    continue;
                }

                AddExplicitFrequencyHint(hints, name, frequency.Value);
            }
        }

        return hints;
    }

    private static EVulkanBindingFrequency? ParseExplicitFrequencyAnnotation(
        Match uniformStatement)
    {
        Group frequencyGroup = uniformStatement.Groups["frequency"];
        if (!frequencyGroup.Success)
            return null;

        string frequencyName = frequencyGroup.Value;
        if (Enum.TryParse(
                frequencyName,
                ignoreCase: true,
                out EVulkanBindingFrequency frequency) &&
            frequency > EVulkanBindingFrequency.Unknown &&
            frequency < EVulkanBindingFrequency.Count)
        {
            return frequency;
        }

        throw new InvalidOperationException(
            $"Unsupported XRENGINE_FREQUENCY annotation '{frequencyName}'. " +
            "Expected Frame, View, Pass, Material, Object, Instance, or RuntimeCallback.");
    }

    private static Dictionary<string, EVulkanBindingFrequency> ParseExplicitFrequencyOverrides(
        string source)
    {
        Dictionary<string, EVulkanBindingFrequency> overrides =
            new(StringComparer.Ordinal);
        foreach (Match match in FrequencyOverrideRegex.Matches(source))
        {
            string name = match.Groups["name"].Value;
            string frequencyName = match.Groups["frequency"].Value;
            if (!Enum.TryParse(
                    frequencyName,
                    ignoreCase: true,
                    out EVulkanBindingFrequency frequency) ||
                frequency <= EVulkanBindingFrequency.Unknown ||
                frequency >= EVulkanBindingFrequency.Count)
            {
                throw new InvalidOperationException(
                    $"Unsupported XRENGINE_FREQUENCY_OVERRIDE frequency '{frequencyName}' for '{name}'. " +
                    "Expected Frame, View, Pass, Material, Object, Instance, or RuntimeCallback.");
            }

            AddExplicitFrequencyHint(overrides, name, frequency);
        }

        return overrides;
    }

    private static void MergeExplicitFrequencyHints(
        Dictionary<string, EVulkanBindingFrequency> destination,
        IReadOnlyDictionary<string, EVulkanBindingFrequency>? source)
    {
        if (source is null)
            return;

        foreach (KeyValuePair<string, EVulkanBindingFrequency> hint in source)
            AddExplicitFrequencyHint(destination, hint.Key, hint.Value);
    }

    private static void AddExplicitFrequencyHint(
        Dictionary<string, EVulkanBindingFrequency> hints,
        string name,
        EVulkanBindingFrequency frequency)
    {
        if (hints.TryGetValue(name, out EVulkanBindingFrequency existing) &&
            existing != frequency)
        {
            throw new InvalidOperationException(
                $"Conflicting explicit Vulkan auto-uniform frequencies for '{name}': " +
                $"{existing} and {frequency}.");
        }

        hints[name] = frequency;
    }

    private static EVulkanBindingFrequency? ResolveExplicitFrequency(
        string uniformName,
        EVulkanBindingFrequency? statementFrequency,
        IReadOnlyDictionary<string, EVulkanBindingFrequency> overrides)
    {
        if (!overrides.TryGetValue(
                uniformName,
                out EVulkanBindingFrequency overrideFrequency))
        {
            return statementFrequency;
        }

        if (statementFrequency.HasValue &&
            statementFrequency.Value != overrideFrequency)
        {
            throw new InvalidOperationException(
                $"Uniform '{uniformName}' has conflicting XRENGINE_FREQUENCY " +
                $"({statementFrequency.Value}) and XRENGINE_FREQUENCY_OVERRIDE " +
                $"({overrideFrequency}) annotations.");
        }

        return overrideFrequency;
    }

}
