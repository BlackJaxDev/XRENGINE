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

}
