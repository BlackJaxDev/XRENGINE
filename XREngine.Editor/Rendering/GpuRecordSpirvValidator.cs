using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Shaderc;
using SPIRVCross;
using XREngine.Rendering;
using XREngine.Rendering.Shaders;

namespace XREngine.Editor.GpuLayouts;

/// <summary>
/// Checks generated std430 storage records against SPIR-V reflection before
/// shader publication. This path runs during cooking or explicit editor checks.
/// </summary>
public static unsafe class GpuRecordSpirvValidator
{
    private static readonly Shaderc ShadercApi = Shaderc.GetApi();

    public static void ValidateGeneratedRecords(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ValidateShaderDirectory(Path.Combine(repositoryRoot, "Build", "CommonAssets", "Shaders"));
    }

    public static void ValidateShaderDirectory(string shaderDirectory)
    {
        ValidateGeneratedRecords(shaderDirectory, "Advanced");
        ValidateGeneratedRecords(shaderDirectory, "GPUScene");
    }

    private static void ValidateGeneratedRecords(string shaderDirectory, string group)
    {
        GpuRecordIncludeGenerator.VerifyShaderDirectory(shaderDirectory, group);
        ValidateDeclarations(GpuRecordIncludeGenerator.Generate(group), group);
    }

    public static void ValidateDeclarations(string declarations, string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declarations);
        Type[] records = GpuRecordIncludeGenerator.GetRecordTypes(group);
        string source = BuildValidationShader(records, declarations);
        byte[] spirv = CompileForReflection(source);
        Validate(spirv, records);
    }

    private static byte[] CompileForReflection(string source)
    {
        Compiler* compiler = ShadercApi.CompilerInitialize();
        if (compiler is null)
            throw new InvalidOperationException("Cannot initialize Shaderc for GPU record validation.");
        CompileOptions* options = ShadercApi.CompileOptionsInitialize();
        if (options is null)
        {
            ShadercApi.CompilerRelease(compiler);
            throw new InvalidOperationException("Cannot allocate Shaderc options for GPU record validation.");
        }

        CompilationResult* result = null;
        try
        {
            // SPIRVCross.NET's native parser does not accept the renderer's
            // SPIR-V 1.6 target. These std430 records need no newer feature.
            ShadercApi.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
            ShadercApi.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, 0x00402000u);
            ShadercApi.CompileOptionsSetTargetSpirv(options, SpirvVersion.Shaderc13);
            ShadercApi.CompileOptionsSetGenerateDebugInfo(options);
            byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
            byte[] nameBytes = Encoding.UTF8.GetBytes("GpuRecordLayoutValidation.comp\0");
            byte[] entryBytes = Encoding.UTF8.GetBytes("main\0");
            fixed (byte* sourcePtr = sourceBytes)
            fixed (byte* namePtr = nameBytes)
            fixed (byte* entryPtr = entryBytes)
            {
                result = ShadercApi.CompileIntoSpv(
                    compiler, sourcePtr, (nuint)sourceBytes.Length,
                    ShaderKind.ComputeShader, namePtr, entryPtr, options);
            }
            if (result is null)
                throw new InvalidOperationException("Shaderc returned no GPU record validation result.");
            if (ShadercApi.ResultGetCompilationStatus(result) != CompilationStatus.Success)
            {
                string detail = Marshal.PtrToStringUTF8((nint)ShadercApi.ResultGetErrorMessage(result)) ?? string.Empty;
                throw new InvalidOperationException($"GPU record validation shader failed to compile: {detail}");
            }
            int byteCount = checked((int)ShadercApi.ResultGetLength(result));
            byte[] spirv = new byte[byteCount];
            Marshal.Copy((nint)ShadercApi.ResultGetBytes(result), spirv, 0, byteCount);
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

    public static void Validate(byte[] spirv, IReadOnlyList<Type> records)
    {
        ArgumentNullException.ThrowIfNull(spirv);
        ArgumentNullException.ThrowIfNull(records);
        if (spirv.Length == 0 || (spirv.Length & 3) != 0)
            throw new ArgumentException("SPIR-V must contain aligned 32-bit words.", nameof(spirv));
        if (BitConverter.ToUInt32(spirv) != 0x07230203u)
            throw new InvalidOperationException($"Shader compiler did not return SPIR-V bytecode (magic 0x{BitConverter.ToUInt32(spirv):X8}).");

        spvc_context context = default;
        Check(SPIRV.spvc_context_create(&context), context, "create reflection context");
        try
        {
            spvc_parsed_ir parsed;
            fixed (byte* bytes = spirv)
                Check(SPIRV.spvc_context_parse_spirv(context, (SpvId*)bytes, (nuint)(spirv.Length / 4), &parsed), context,
                    $"parse SPIR-V version 0x{BitConverter.ToUInt32(spirv, 4):X8} ({spirv.Length} bytes)");

            spvc_compiler compiler;
            Check(SPIRV.spvc_context_create_compiler(context, spvc_backend.None, parsed, spvc_capture_mode.TakeOwnership, &compiler), context, "create reflection compiler");
            spvc_resources resources;
            Check(SPIRV.spvc_compiler_create_shader_resources(compiler, &resources), context, "enumerate shader resources");
            // SPIRVCross.NET exposes this native pointer-to-pointer output as a
            // single pointer. Supply pointer-sized storage for the returned list.
            nint storageBufferAddress = 0;
            nuint storageBufferCount;
            Check(SPIRV.spvc_resources_get_resource_list_for_type(
                resources, spvc_resource_type.StorageBuffer, (spvc_reflected_resource*)&storageBufferAddress, &storageBufferCount), context, "enumerate storage buffers");
            spvc_reflected_resource* storageBuffers = (spvc_reflected_resource*)storageBufferAddress;

            bool[] found = new bool[records.Count];
            for (nuint resourceIndex = 0; resourceIndex < storageBufferCount; ++resourceIndex)
            {
                spvc_reflected_resource resource = storageBuffers[resourceIndex];
                uint binding = SPIRV.spvc_compiler_get_decoration(
                    compiler, resource.id, SpvDecoration.SpvDecorationBinding);
                if (binding >= records.Count)
                    continue;
                int index = checked((int)binding);
                if (found[index])
                    throw new InvalidOperationException($"Duplicate GPU record validation binding {binding}.");
                found[index] = true;
                ValidateResource(context, compiler, resource, records[index]);
            }

            for (int index = 0; index < found.Length; ++index)
            {
                if (!found[index])
                    throw new InvalidOperationException($"GPU record {records[index].FullName} is absent from compiled shader reflection.");
            }
        }
        finally
        {
            SPIRV.spvc_context_destroy(context);
        }
    }

    private static void ValidateResource(spvc_context context, spvc_compiler compiler, spvc_reflected_resource resource, Type record)
    {
        spvc_type block = SPIRV.spvc_compiler_get_type_handle(compiler, resource.base_type_id);
        if (SPIRV.spvc_type_get_num_member_types(block) != 1)
            throw new InvalidOperationException($"GPU record block {record.Name} must contain exactly one runtime array.");

        uint stride;
        Check(SPIRV.spvc_compiler_type_struct_member_array_stride(compiler, block, 0, &stride), context, $"reflect {record.Name} array stride");
        int marshalledSize = Marshal.SizeOf(record);
        int unsafeSize = GetUnsafeSize(record);
        if (stride != marshalledSize || unsafeSize != marshalledSize)
            throw new InvalidOperationException($"GPU record {record.Name} stride mismatch: shader={stride}, Marshal.SizeOf={marshalledSize}, Unsafe.SizeOf={unsafeSize}.");

        uint arrayTypeId = SPIRV.spvc_type_get_member_type(block, 0);
        spvc_type arrayType = SPIRV.spvc_compiler_get_type_handle(compiler, arrayTypeId);
        uint recordTypeId = SPIRV.spvc_type_get_base_type_id(arrayType);
        spvc_type recordType = SPIRV.spvc_compiler_get_type_handle(compiler, recordTypeId);
        FieldInfo[] fields = GpuRecordIncludeGenerator.GetStorageFields(record);
        uint reflectedCount = SPIRV.spvc_type_get_num_member_types(recordType);
        if (reflectedCount != fields.Length)
            throw new InvalidOperationException($"GPU record {record.Name} member count mismatch: shader={reflectedCount}, C#={fields.Length}.");

        for (int fieldIndex = 0; fieldIndex < fields.Length; ++fieldIndex)
        {
            FieldInfo field = fields[fieldIndex];
            string managedName = GpuRecordIncludeGenerator.GetStorageFieldName(field);
            string expectedName = GpuRecordIncludeGenerator.GetGlslMemberName(record, managedName);
            uint shaderIndex = reflectedCount;
            for (uint candidate = 0; candidate < reflectedCount; ++candidate)
            {
                string shaderName = Marshal.PtrToStringUTF8((nint)SPIRV.spvc_compiler_get_member_name(compiler, recordTypeId, candidate)) ?? string.Empty;
                if (string.Equals(shaderName, expectedName, StringComparison.Ordinal))
                {
                    shaderIndex = candidate;
                    break;
                }
            }
            if (shaderIndex == reflectedCount)
                throw new InvalidOperationException($"GPU record {record.Name}.{managedName} is absent from shader member names.");

            uint shaderOffset;
            Check(SPIRV.spvc_compiler_type_struct_member_offset(compiler, recordType, shaderIndex, &shaderOffset), context, $"reflect {record.Name}.{managedName} offset");
            int managedOffset = Marshal.OffsetOf(record, field.Name).ToInt32();
            if (shaderOffset != managedOffset)
                throw new InvalidOperationException($"GPU record {record.Name}.{managedName} offset mismatch: shader={shaderOffset}, C#={managedOffset}.");

            if (field.FieldType == typeof(System.Numerics.Matrix4x4))
            {
                uint matrixStride;
                Check(SPIRV.spvc_compiler_type_struct_member_matrix_stride(compiler, recordType, shaderIndex, &matrixStride), context, $"reflect {record.Name}.{managedName} matrix stride");
                bool rowMajor = SPIRV.spvc_compiler_has_member_decoration(
                    compiler, recordTypeId, shaderIndex, SpvDecoration.SpvDecorationRowMajor);
                if (matrixStride != 16 || !rowMajor)
                    throw new InvalidOperationException($"GPU record {record.Name}.{managedName} matrix layout mismatch: shader stride={matrixStride}, rowMajor={rowMajor}; C# requires row-major stride 16.");
            }
        }
    }

    private static string BuildValidationShader(IReadOnlyList<Type> records, string declarations)
    {
        StringBuilder source = new(24_576);
        source.AppendLine("#version 460");
        source.AppendLine("layout(local_size_x = 1) in;");
        source.AppendLine(declarations);
        source.Append("layout(set = 0, binding = ").Append(records.Count)
            .Append(", std430) buffer XRValidationSink { uint values[")
            .Append(records.Count).AppendLine("]; } xr_validation_sink;");
        for (int index = 0; index < records.Count; ++index)
        {
            string name = records[index].GetCustomAttribute<GpuRecordAttribute>()!.GlslName;
            source.Append("layout(set = 0, binding = ").Append(index)
                .Append(", std430, row_major) readonly buffer XRValidationBlock_").Append(index)
                .Append(" { ").Append(name).Append(" records[]; } xr_validation_")
                .Append(index).AppendLine(";");
        }
        source.AppendLine("void main() {");
        for (int index = 0; index < records.Count; ++index)
        {
            source.Append("    xr_validation_sink.values[").Append(index)
                .Append("] = ")
                .Append(GetUintReadExpression(records[index], $"xr_validation_{index}.records[0]"))
                .AppendLine(";");
        }
        source.AppendLine("}");
        return source.ToString();
    }

    private static string GetUintReadExpression(Type type, string value)
    {
        FieldInfo field = GpuRecordIncludeGenerator.GetStorageFields(type)[0];
        string expression = value + "." + GpuRecordIncludeGenerator.GetGlslMemberName(type, GpuRecordIncludeGenerator.GetStorageFieldName(field));
        Type memberType = field.FieldType.IsEnum ? Enum.GetUnderlyingType(field.FieldType) : field.FieldType;
        if (memberType.GetCustomAttribute<GpuRecordAttribute>() is not null)
            return GetUintReadExpression(memberType, expression);
        if (memberType == typeof(uint)) return expression;
        if (memberType == typeof(int)) return $"uint({expression})";
        if (memberType == typeof(float)) return $"floatBitsToUint({expression})";
        if (memberType == typeof(ulong)) return expression + ".x";
        if (memberType == typeof(System.Numerics.Matrix4x4)) return $"floatBitsToUint({expression}[0][0])";
        if (memberType == typeof(System.Numerics.Vector2) || memberType == typeof(System.Numerics.Vector4))
            return $"floatBitsToUint({expression}.x)";
        throw new NotSupportedException($"Cannot read {type.Name}.{field.Name} for GPU record validation.");
    }

    private static int GetUnsafeSize(Type type)
    {
        MethodInfo method = typeof(GpuRecordSpirvValidator).GetMethod(nameof(UnsafeSize), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(type);
        return (int)method.Invoke(null, null)!;
    }

    private static int UnsafeSize<T>() where T : unmanaged => Unsafe.SizeOf<T>();

    private static void Check(spvc_result result, spvc_context context, string action)
    {
        if (result == spvc_result.SPVC_SUCCESS)
            return;
        string detail = Marshal.PtrToStringUTF8((nint)SPIRV.spvc_context_get_last_error_string(context)) ?? string.Empty;
        throw new InvalidOperationException($"Cannot {action}: {detail}");
    }
}
