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
    private static string ApplyVulkanSourceFixups(string source, EShaderType shaderType, bool useVulkanClipDepthRemap)
    {
        string rewritten = source.Replace("gl_InstanceID", "gl_InstanceIndex", StringComparison.Ordinal);
        rewritten = rewritten.Replace("gl_VertexID", "gl_VertexIndex", StringComparison.Ordinal);
        rewritten = FloatSuffixRegex.Replace(rewritten, "${num}");
        rewritten = EnsureIOLocationQualifiers(rewritten);
        rewritten = ApplyVulkanClipDepthRemap(rewritten, shaderType, useVulkanClipDepthRemap);
        return rewritten;
    }

    private static string ApplyVulkanClipDepthRemap(string source, EShaderType shaderType, bool useVulkanClipDepthRemap)
    {
        if (!useVulkanClipDepthRemap)
            return source;

        if (source.Contains("XRENGINE_ApplyVulkanClipDepthRemap", StringComparison.Ordinal) ||
            source.Contains("XRENGINE_RemapVulkanClipDepth", StringComparison.Ordinal))
            return source;

        return shaderType switch
        {
            EShaderType.Vertex or EShaderType.TessEvaluation => ApplyVulkanClipDepthRemapAfterMain(source),
            EShaderType.Geometry => ApplyVulkanClipDepthRemapBeforeGeometryEmit(source),
            EShaderType.Mesh => ApplyVulkanClipDepthRemapToMeshPositionAssignments(source),
            _ => source,
        };
    }

    private static string ApplyVulkanClipDepthRemapAfterMain(string source)
    {
        Match mainMatch = ShaderMainFunctionRegex.Match(source);
        if (!mainMatch.Success)
            return source;

        StringBuilder builder = new(source.Length + 256);
        builder.Append(source, 0, mainMatch.Index);
        builder.Append("void XRENGINE_UserMain()\n{");
        builder.Append(source, mainMatch.Index + mainMatch.Length, source.Length - (mainMatch.Index + mainMatch.Length));
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("void XRENGINE_ApplyVulkanClipDepthRemap()");
        builder.AppendLine("{");
        builder.AppendLine("    gl_Position.z = gl_Position.z * 0.5 + gl_Position.w * 0.5;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("void main()");
        builder.AppendLine("{");
        builder.AppendLine("    XRENGINE_UserMain();");
        builder.AppendLine("    XRENGINE_ApplyVulkanClipDepthRemap();");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string ApplyVulkanClipDepthRemapBeforeGeometryEmit(string source)
    {
        if (!GeometryEmitVertexRegex.IsMatch(source))
            return source;

        int insertIndex = FindAutoUniformInsertionIndex(source);
        if (insertIndex < 0)
            insertIndex = 0;

        const string helper = """
void XRENGINE_ApplyVulkanClipDepthRemap()
{
    gl_Position.z = gl_Position.z * 0.5 + gl_Position.w * 0.5;
}

""";

        string withHelper = source.Insert(insertIndex, helper);
        return GeometryEmitVertexRegex.Replace(
            withHelper,
            match => $"{match.Groups["indent"].Value}XRENGINE_ApplyVulkanClipDepthRemap();\n{match.Groups["indent"].Value}EmitVertex();");
    }

    private static string ApplyVulkanClipDepthRemapToMeshPositionAssignments(string source)
    {
        if (!MeshPositionAssignmentRegex.IsMatch(source))
            return source;

        int insertIndex = FindAutoUniformInsertionIndex(source);
        if (insertIndex < 0)
            insertIndex = 0;

        const string helper = """
vec4 XRENGINE_RemapVulkanClipDepth(vec4 position)
{
    position.z = position.z * 0.5 + position.w * 0.5;
    return position;
}

""";

        string withHelper = source.Insert(insertIndex, helper);
        return MeshPositionAssignmentRegex.Replace(
            withHelper,
            match => $"{match.Groups["target"].Value}= XRENGINE_RemapVulkanClipDepth({match.Groups["expr"].Value});");
    }
}
