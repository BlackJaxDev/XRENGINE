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

public readonly record struct DescriptorBindingInfo(
    uint Set,
    uint Binding,
    DescriptorType DescriptorType,
    ShaderStageFlags StageFlags,
    uint Count,
    string Name,
    ImageViewType? ExpectedImageViewType = null);

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
    IReadOnlyList<AutoUniformDefaultValue>? DefaultArrayValues,
    IReadOnlyList<AutoUniformMember>? StructMembers = null);

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

internal sealed record VulkanTransformFeedbackBufferCapture(
    uint Binding,
    EFeedbackType Type,
    IReadOnlyList<string> Names);

internal sealed record VulkanTransformFeedbackCompilePlan(IReadOnlyList<VulkanTransformFeedbackBufferCapture> Buffers)
{
    public bool HasCaptures => Buffers.Count > 0;
    public string Identity { get; } = BuildIdentity(Buffers);

    public static VulkanTransformFeedbackCompilePlan Empty { get; } = new(Array.Empty<VulkanTransformFeedbackBufferCapture>());

    private static string BuildIdentity(IReadOnlyList<VulkanTransformFeedbackBufferCapture> buffers)
    {
        if (buffers.Count == 0)
            return "TransformFeedback=<none>";

        StringBuilder builder = new("TransformFeedback=");
        for (int i = 0; i < buffers.Count; i++)
        {
            if (i > 0)
                builder.Append(';');

            VulkanTransformFeedbackBufferCapture buffer = buffers[i];
            builder
                .Append(buffer.Binding.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(buffer.Type)
                .Append(':')
                .AppendJoin('|', buffer.Names);
        }

        return builder.ToString();
    }
}

internal sealed record GlslStructDefinition(
    string Name,
    IReadOnlyList<GlslStructField> Fields);

internal readonly record struct GlslStructField(
    string GlslType,
    string Name,
    bool IsArray,
    uint ArrayLength);
