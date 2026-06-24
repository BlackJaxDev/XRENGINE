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

internal static class VulkanShaderTransformFeedback
{
    private static readonly Regex VersionDirectiveRegex = new(
        @"^\s*#\s*version\b[^\r\n]*(?:\r?\n)?",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex TransformFeedbackExtensionRegex = new(
        @"^\s*#\s*extension\s+GL_EXT_transform_feedback\s*:\s*\w+\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex OutputDeclarationRegex = new(
        @"(?m)^(?<indent>\s*)(?<layout>layout\s*\((?<layoutArgs>[^)]*)\)\s*)?(?<qualifiers>(?:(?:flat|noperspective|smooth|centroid|sample|invariant|precise|patch|highp|mediump|lowp)\s+)*)out\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<array>\s*(?:\[[^\]]*\]\s*)*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LocationQualifierRegex = new(
        @"(?:^|,)\s*location\s*=\s*(?<location>\d+)\s*(?:,|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex XfbQualifierRegex = new(
        @"\s*(?:xfb_buffer|xfb_offset|xfb_stride)\s*=\s*[^,]+,?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ArrayLengthRegex = new(
        @"\[(?<length>\d+)u?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private sealed record OutputDeclaration(
        string Name,
        string GlslType,
        string ArraySuffix,
        string? LayoutArgs,
        Match Match);

    private readonly record struct XfbVariableInfo(uint Buffer, uint Offset, uint Location);

    public static string Rewrite(string source, EShaderType shaderType, VulkanTransformFeedbackCompilePlan? plan)
    {
        if (plan is not { HasCaptures: true })
            return source;

        if (!CanCarryTransformFeedback(shaderType))
        {
            throw new InvalidOperationException(
                $"Vulkan transform feedback can be declared only on vertex, tessellation evaluation, or geometry shader outputs; got {shaderType}.");
        }

        Dictionary<string, OutputDeclaration> outputs = ParseOutputDeclarations(source);
        if (outputs.Count == 0)
            throw new InvalidOperationException("Vulkan transform feedback requested captures, but the capture shader declares no global output variables.");

        Dictionary<string, XfbVariableInfo> variables = BuildVariableInfo(plan, outputs);
        Dictionary<uint, uint> strides = BuildBufferStrides(plan, outputs);
        string rewritten = OutputDeclarationRegex.Replace(source, match =>
        {
            string name = match.Groups["name"].Value;
            if (!variables.TryGetValue(name, out XfbVariableInfo xfb))
                return match.Value;

            string layoutArgs = MergeLayoutQualifiers(
                match.Groups["layoutArgs"].Success ? match.Groups["layoutArgs"].Value : null,
                xfb.Buffer,
                xfb.Offset,
                xfb.Location);
            string indent = match.Groups["indent"].Value;
            string qualifiers = match.Groups["qualifiers"].Value;
            string type = match.Groups["type"].Value;
            string array = match.Groups["array"].Value;

            return $"{indent}layout({layoutArgs}) {qualifiers}out {type} {name}{array};";
        });

        return InjectExtensionAndStrideLayouts(rewritten, strides);
    }

    private static bool CanCarryTransformFeedback(EShaderType shaderType)
        => shaderType == EShaderType.Vertex ||
           shaderType == EShaderType.TessEvaluation ||
           shaderType == EShaderType.Geometry;

    private static Dictionary<string, OutputDeclaration> ParseOutputDeclarations(string source)
    {
        Dictionary<string, OutputDeclaration> outputs = new(StringComparer.Ordinal);
        foreach (Match match in OutputDeclarationRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string name = match.Groups["name"].Value;
            outputs[name] = new OutputDeclaration(
                name,
                match.Groups["type"].Value,
                match.Groups["array"].Value,
                match.Groups["layoutArgs"].Success ? match.Groups["layoutArgs"].Value : null,
                match);
        }

        return outputs;
    }

    private static Dictionary<string, XfbVariableInfo> BuildVariableInfo(
        VulkanTransformFeedbackCompilePlan plan,
        IReadOnlyDictionary<string, OutputDeclaration> outputs)
    {
        Dictionary<string, XfbVariableInfo> variables = new(StringComparer.Ordinal);
        uint nextAutoLocation = ResolveNextAutoLocation(outputs.Values);

        foreach (VulkanTransformFeedbackBufferCapture buffer in plan.Buffers)
        {
            uint offset = 0;
            foreach (string name in buffer.Names)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!outputs.TryGetValue(name, out OutputDeclaration? declaration))
                {
                    throw new InvalidOperationException(
                        $"Vulkan transform feedback varying '{name}' was requested but was not found as a global output in the capture shader.");
                }

                if (variables.ContainsKey(name))
                    throw new InvalidOperationException($"Vulkan transform feedback varying '{name}' was requested more than once.");

                uint location = TryGetExistingLocation(declaration.LayoutArgs, out uint existingLocation)
                    ? existingLocation
                    : nextAutoLocation++;
                variables[name] = new XfbVariableInfo(buffer.Binding, offset, location);
                offset += ResolveOutputByteSize(declaration);
            }
        }

        return variables;
    }

    private static Dictionary<uint, uint> BuildBufferStrides(
        VulkanTransformFeedbackCompilePlan plan,
        IReadOnlyDictionary<string, OutputDeclaration> outputs)
    {
        Dictionary<uint, uint> strides = new();
        foreach (VulkanTransformFeedbackBufferCapture buffer in plan.Buffers)
        {
            uint stride = 0;
            foreach (string name in buffer.Names)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!outputs.TryGetValue(name, out OutputDeclaration? declaration))
                    continue;

                stride += ResolveOutputByteSize(declaration);
            }

            if (stride == 0)
                continue;

            if (!strides.TryAdd(buffer.Binding, stride))
            {
                throw new InvalidOperationException(
                    $"Vulkan transform feedback binding {buffer.Binding} is declared by more than one XRTransformFeedback object.");
            }
        }

        return strides;
    }

    private static uint ResolveNextAutoLocation(IEnumerable<OutputDeclaration> declarations)
    {
        uint next = 0;
        foreach (OutputDeclaration declaration in declarations)
        {
            if (TryGetExistingLocation(declaration.LayoutArgs, out uint location))
                next = Math.Max(next, location + 1);
        }

        return next;
    }

    private static bool TryGetExistingLocation(string? layoutArgs, out uint location)
    {
        location = 0;
        if (string.IsNullOrWhiteSpace(layoutArgs))
            return false;

        Match match = LocationQualifierRegex.Match(layoutArgs);
        if (!match.Success)
            return false;

        return uint.TryParse(match.Groups["location"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out location);
    }

    private static string MergeLayoutQualifiers(string? existingLayoutArgs, uint buffer, uint offset, uint location)
    {
        List<string> qualifiers = [];
        bool hasLocation = false;
        if (!string.IsNullOrWhiteSpace(existingLayoutArgs))
        {
            string cleaned = XfbQualifierRegex.Replace(existingLayoutArgs, string.Empty);
            foreach (string qualifier in cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (qualifier.Length == 0)
                    continue;

                if (qualifier.StartsWith("location", StringComparison.OrdinalIgnoreCase))
                    hasLocation = true;

                qualifiers.Add(qualifier);
            }
        }

        if (!hasLocation)
            qualifiers.Add("location = " + location.ToString(CultureInfo.InvariantCulture));

        qualifiers.Add("xfb_buffer = " + buffer.ToString(CultureInfo.InvariantCulture));
        qualifiers.Add("xfb_offset = " + offset.ToString(CultureInfo.InvariantCulture));
        return string.Join(", ", qualifiers);
    }

    private static string InjectExtensionAndStrideLayouts(string source, IReadOnlyDictionary<uint, uint> strides)
    {
        int insertionIndex = ResolveDirectiveInsertionIndex(source);
        StringBuilder prefix = new();
        if (!TransformFeedbackExtensionRegex.IsMatch(source))
            prefix.AppendLine("#extension GL_EXT_transform_feedback : require");

        foreach (KeyValuePair<uint, uint> stride in strides.OrderBy(static pair => pair.Key))
        {
            prefix
                .Append("layout(xfb_buffer = ")
                .Append(stride.Key.ToString(CultureInfo.InvariantCulture))
                .Append(", xfb_stride = ")
                .Append(stride.Value.ToString(CultureInfo.InvariantCulture))
                .AppendLine(") out;");
        }

        if (prefix.Length == 0)
            return source;

        return source.Insert(insertionIndex, prefix.ToString());
    }

    private static int ResolveDirectiveInsertionIndex(string source)
    {
        Match versionMatch = VersionDirectiveRegex.Match(source);
        int insertionIndex = versionMatch.Success
            ? versionMatch.Index + versionMatch.Length
            : 0;

        while (insertionIndex < source.Length)
        {
            int lineEnd = source.IndexOf('\n', insertionIndex);
            int lineLength = (lineEnd >= 0 ? lineEnd : source.Length) - insertionIndex;
            ReadOnlySpan<char> line = source.AsSpan(insertionIndex, lineLength).TrimStart();
            if (!line.StartsWith("#extension", StringComparison.OrdinalIgnoreCase))
                break;

            insertionIndex = lineEnd >= 0 ? lineEnd + 1 : source.Length;
        }

        return insertionIndex;
    }

    private static uint ResolveOutputByteSize(OutputDeclaration declaration)
    {
        if (!TryResolveScalarByteSize(declaration.GlslType, out uint baseSize, out uint componentCount))
        {
            throw new InvalidOperationException(
                $"Vulkan transform feedback cannot infer byte size for output '{declaration.Name}' of type '{declaration.GlslType}'. " +
                "Capture scalar, vector, or matrix GLSL outputs, or split complex outputs into supported variables.");
        }

        uint arrayLength = ResolveArrayElementCount(declaration.ArraySuffix);
        return checked(baseSize * componentCount * arrayLength);
    }

    private static uint ResolveArrayElementCount(string arraySuffix)
    {
        if (string.IsNullOrWhiteSpace(arraySuffix))
            return 1;

        uint count = 1;
        bool foundLength = false;
        foreach (Match match in ArrayLengthRegex.Matches(arraySuffix))
        {
            foundLength = true;
            if (!uint.TryParse(match.Groups["length"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint length) || length == 0)
                throw new InvalidOperationException("Vulkan transform feedback array outputs must use constant positive integer lengths.");

            count = checked(count * length);
        }

        if (!foundLength)
            throw new InvalidOperationException("Vulkan transform feedback array outputs must use constant positive integer lengths.");

        return count;
    }

    private static bool TryResolveScalarByteSize(string glslType, out uint scalarBytes, out uint componentCount)
    {
        scalarBytes = 4;
        componentCount = 1;

        switch (glslType)
        {
            case "bool":
            case "int":
            case "uint":
            case "float":
                return true;
            case "double":
                scalarBytes = 8;
                return true;
        }

        if (TryResolveVector(glslType, "vec", 4, out scalarBytes, out componentCount) ||
            TryResolveVector(glslType, "ivec", 4, out scalarBytes, out componentCount) ||
            TryResolveVector(glslType, "uvec", 4, out scalarBytes, out componentCount) ||
            TryResolveVector(glslType, "bvec", 4, out scalarBytes, out componentCount) ||
            TryResolveVector(glslType, "dvec", 8, out scalarBytes, out componentCount))
        {
            return true;
        }

        if (TryResolveMatrix(glslType, "mat", 4, out scalarBytes, out componentCount) ||
            TryResolveMatrix(glslType, "dmat", 8, out scalarBytes, out componentCount))
        {
            return true;
        }

        return false;
    }

    private static bool TryResolveVector(string glslType, string prefix, uint bytes, out uint scalarBytes, out uint componentCount)
    {
        scalarBytes = bytes;
        componentCount = 0;
        if (!glslType.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        string suffix = glslType[prefix.Length..];
        if (!uint.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint count) || count < 2 || count > 4)
            return false;

        componentCount = count;
        return true;
    }

    private static bool TryResolveMatrix(string glslType, string prefix, uint bytes, out uint scalarBytes, out uint componentCount)
    {
        scalarBytes = bytes;
        componentCount = 0;
        if (!glslType.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        string suffix = glslType[prefix.Length..];
        if (suffix.Length == 0)
            return false;

        uint columns;
        uint rows;
        int xIndex = suffix.IndexOf('x');
        if (xIndex < 0)
        {
            if (!uint.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out columns) || columns < 2 || columns > 4)
                return false;

            rows = columns;
        }
        else
        {
            if (!uint.TryParse(suffix[..xIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out columns) ||
                !uint.TryParse(suffix[(xIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out rows) ||
                columns < 2 || columns > 4 || rows < 2 || rows > 4)
            {
                return false;
            }
        }

        componentCount = columns * rows;
        return true;
    }
}
