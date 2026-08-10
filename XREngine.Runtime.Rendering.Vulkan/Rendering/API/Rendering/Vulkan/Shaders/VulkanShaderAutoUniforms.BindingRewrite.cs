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

            // Skip interface blocks (type name followed by '{' after semicolon — but these would have { not ; so regex won't match).
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
    /// preprocessor conditional blocks (#ifdef / #ifndef / #if … #endif).
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

    private static string RewriteOpaqueUniformBindings(
        string source,
        EShaderType shaderType,
        IReadOnlyDictionary<string, EVulkanBindingFrequency>?
            explicitFrequencyHints)
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
            uint descriptorSet = ResolveOpaqueDescriptorSet(
                declaration,
                explicitFrequencyHints);
            string existingLayout = match.Groups["layout"].Value;
            string layoutPrefix;
            if (!string.IsNullOrWhiteSpace(existingLayout))
            {
                bool hasBinding = existingLayout.Contains("binding", StringComparison.OrdinalIgnoreCase);
                layoutPrefix = hasBinding
                    ? EnsureLayoutHasSet(existingLayout, descriptorSet)
                    : EnsureLayoutHasSetAndBinding(existingLayout, descriptorSet, nextBinding++);
            }
            else
            {
                layoutPrefix = $"layout(set = {descriptorSet}, binding = {nextBinding++}) ";
            }

            return $"{layoutPrefix}uniform {glslType} {declaration};";
        });
    }

    /// <summary>
    /// Maps an explicitly owned opaque resource to the descriptor tier whose
    /// lifetime matches that ownership. Unannotated resources retain the legacy
    /// material tier so existing shaders do not change behavior implicitly.
    /// </summary>
    private static uint ResolveOpaqueDescriptorSet(
        string declaration,
        IReadOnlyDictionary<string, EVulkanBindingFrequency>?
            explicitFrequencyHints)
    {
        if (explicitFrequencyHints is null ||
            explicitFrequencyHints.Count == 0)
        {
            return VulkanDescriptorManager.MaterialSetIndex;
        }

        uint resolvedSet = VulkanDescriptorManager.MaterialSetIndex;
        bool hasExplicitOwner = false;
        foreach (string declarator in SplitDeclarators(declaration))
        {
            Match nameMatch = Regex.Match(
                declarator,
                @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)",
                RegexOptions.CultureInvariant);
            if (!nameMatch.Success ||
                !explicitFrequencyHints.TryGetValue(
                    nameMatch.Groups["name"].Value,
                    out EVulkanBindingFrequency frequency))
            {
                continue;
            }

            uint candidateSet = frequency switch
            {
                EVulkanBindingFrequency.Material =>
                    VulkanDescriptorManager.MaterialSetIndex,
                EVulkanBindingFrequency.Frame or
                EVulkanBindingFrequency.View =>
                    VulkanDescriptorManager.GlobalsSetIndex,
                _ => VulkanDescriptorManager.PerPassSetIndex,
            };
            if (hasExplicitOwner && candidateSet != resolvedSet)
            {
                throw new InvalidOperationException(
                    $"Opaque uniform declaration '{declaration}' mixes descriptor ownership tiers.");
            }

            resolvedSet = candidateSet;
            hasExplicitOwner = true;
        }

        return resolvedSet;
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

}
