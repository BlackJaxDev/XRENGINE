using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using XREngine.Rendering.Shaders;

namespace XREngine.Editor.GpuLayouts;

/// <summary>
/// Produces the checked-in advanced storage-buffer declarations from their C# records.
/// This runs in editor tooling, outside the render frame and NativeAOT runtime.
/// </summary>
public static class GpuRecordIncludeGenerator
{
    public const string RelativeIncludePath = "Build/CommonAssets/Shaders/Advanced/Generated/AdvancedRecords.glslinc";
    public const string SceneRelativeIncludePath = "Build/CommonAssets/Shaders/Advanced/Generated/GPUSceneRecords.glslinc";

    public static string Generate(string group = "Advanced")
    {
        Type[] records = GetRecordTypes(group);
        StringBuilder source = new(16_384);
        string guard = group == "Advanced" ? "XR_ADVANCED_GENERATED_RECORDS_GLSLINC" : "XR_GPU_SCENE_GENERATED_RECORDS_GLSLINC";
        source.Append("#ifndef ").AppendLine(guard);
        source.Append("#define ").AppendLine(guard);
        source.AppendLine();
        source.AppendLine("// Generated from the C# GPU records. Regenerate with GpuRecordIncludeGenerator.");
        source.AppendLine();
        foreach (Type record in records)
        {
            FieldInfo[] fields = GetStorageFields(record);
            (string Type, string Name)[] members = new (string, string)[fields.Length];
            for (int index = 0; index < fields.Length; ++index)
            {
                FieldInfo field = fields[index];
                members[index] = (
                    GetGlslType(field.FieldType),
                    GetGlslMemberName(record, GetStorageFieldName(field)));
            }

            GpuRecordGlslDeclaration.AppendStruct(
                source,
                record.GetCustomAttribute<GpuRecordAttribute>()!.GlslName,
                members,
                static member => member.Type,
                static member => member.Name);
            source.AppendLine();
        }
        source.AppendLine("#endif");
        return source.ToString();
    }

    public static void Write(string repositoryRoot, string group = "Advanced")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        string relativePath = GetRelativeIncludePath(group);
        string path = Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Generate(group), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static void Verify(string repositoryRoot, string group = "Advanced")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        VerifyShaderDirectory(Path.Combine(repositoryRoot, "Build", "CommonAssets", "Shaders"), group);
    }

    public static void VerifyShaderDirectory(string shaderDirectory, string group = "Advanced")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shaderDirectory);
        string relativePath = GetRelativeIncludePath(group);
        string path = Path.Combine(shaderDirectory, "Advanced", "Generated", Path.GetFileName(relativePath));
        if (!File.Exists(path))
            throw new FileNotFoundException("Generated GPU record declarations are missing.", path);
        string expected = Generate(group).Replace("\r\n", "\n", StringComparison.Ordinal);
        string actual = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"GPU record declarations are stale: {relativePath}. Regenerate before cooking shaders.");
    }

    public static Type[] GetRecordTypes(string group = "Advanced")
    {
        _ = GetRelativeIncludePath(group);
        Type[] marked = typeof(AdvancedShaderAccessLibrary).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<GpuRecordAttribute>() is { } attribute && attribute.Group == group)
            .ToArray();
        Dictionary<Type, byte> visited = new();
        List<Type> ordered = new(marked.Length);
        foreach (Type record in marked.OrderBy(static type => type.FullName, StringComparer.Ordinal))
            Visit(record);
        return [.. ordered];

        void Visit(Type record)
        {
            if (visited.TryGetValue(record, out byte state))
            {
                if (state == 1)
                    throw new InvalidOperationException($"GPU record dependency cycle at {record.FullName}.");
                return;
            }
            visited.Add(record, 1);
            foreach (FieldInfo field in GetStorageFields(record))
            {
                if (field.FieldType.GetCustomAttribute<GpuRecordAttribute>() is not null)
                    Visit(field.FieldType);
                else
                    _ = GetGlslType(field.FieldType);
            }
            visited[record] = 2;
            ordered.Add(record);
        }
    }

    private static string GetRelativeIncludePath(string group)
        => group switch
        {
            "Advanced" => RelativeIncludePath,
            "GPUScene" => SceneRelativeIncludePath,
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown GPU record group."),
        };

    public static FieldInfo[] GetStorageFields(Type record)
    {
        if (record.GetCustomAttribute<GpuRecordAttribute>() is null)
            throw new ArgumentException($"{record.FullName} is not a GPU record.", nameof(record));
        if (!record.IsValueType || record.StructLayoutAttribute?.Value != LayoutKind.Sequential)
            throw new InvalidOperationException($"{record.FullName} must have sequential value-type layout.");
        FieldInfo[] fields = record.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (FieldInfo field in fields)
        {
            bool compilerProperty = field.IsDefined(typeof(CompilerGeneratedAttribute), false) &&
                field.Name.StartsWith('<') && field.Name.EndsWith(">k__BackingField", StringComparison.Ordinal);
            if (!field.IsPublic && !compilerProperty)
                throw new InvalidOperationException($"{record.FullName}.{field.Name} is a private GPU field without a public record property.");
        }
        Array.Sort(fields, (left, right) => Marshal.OffsetOf(record, left.Name).ToInt32()
            .CompareTo(Marshal.OffsetOf(record, right.Name).ToInt32()));
        for (int index = 0; index < fields.Length; ++index)
        {
            FieldInfo field = fields[index];
            if (field.FieldType != typeof(System.Numerics.Vector3))
                continue;
            GpuPaddedVector3Attribute? padding = field.GetCustomAttribute<GpuPaddedVector3Attribute>();
            bool valid = padding is not null && index + 1 < fields.Length
                && fields[index + 1].Name == padding.PaddingFieldName
                && fields[index + 1].FieldType == typeof(float)
                && Marshal.OffsetOf(record, field.Name).ToInt32() % 16 == 0
                && Marshal.OffsetOf(record, fields[index + 1].Name).ToInt32()
                    == Marshal.OffsetOf(record, field.Name).ToInt32() + 12;
            if (!valid)
                throw new NotSupportedException($"{record.FullName}.{field.Name} requires an explicit adjacent float pad for std430 Vector3 storage.");
        }
        return fields;
    }

    public static string GetStorageFieldName(FieldInfo field)
    {
        string name = field.Name;
        if (name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
            return name[1..name.IndexOf('>')];
        return name;
    }

    public static string GetGlslMemberName(Type record, string fieldName)
    {
        if (record.GetCustomAttribute<GpuRecordAttribute>() is { } attribute
            && (attribute.Group == "GPUScene" || attribute.PreserveMemberCase))
            return fieldName;
        // These four public C# names have established shader spellings.
        if (record.Name == "AdvancedBufferReference" && fieldName == "Buffer")
            return "bufferHandle";
        if (record.Name == "AdvancedMaterialTextureBinding" && fieldName == "Texture")
            return "textureReference";
        if (record.Name == "AdvancedMaterialTextureBinding" && fieldName == "Sampler")
            return "samplerReference";
        if (record.Name == "AdvancedShadowRecord" && fieldName == "Texture")
            return "textureReference";
        if (record.Name == "AdvancedSamplerRecord" && fieldName == "Filter")
            return "filterMode";
        return char.ToLowerInvariant(fieldName[0]) + fieldName[1..];
    }

    public static string GetGlslType(Type type)
    {
        if (type.IsEnum)
            type = Enum.GetUnderlyingType(type);
        if (type == typeof(uint)) return "uint";
        if (type == typeof(int)) return "int";
        if (type == typeof(float)) return "float";
        if (type == typeof(ulong)) return "uvec2";
        if (type == typeof(System.Numerics.Vector2)) return "vec2";
        if (type == typeof(System.Numerics.Vector4)) return "vec4";
        if (type == typeof(System.Numerics.Matrix4x4)) return "mat4";
        if (type == typeof(System.Numerics.Vector3)) return "vec3";
        if (type.GetCustomAttribute<GpuRecordAttribute>() is { } nested)
            return nested.GlslName;
        throw new NotSupportedException($"GPU record member type {type.FullName} has no GLSL storage mapping.");
    }
}
