using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Verifies a managed sequential/explicit-layout provider against ABI member offsets.
/// </summary>
public static class ShaderAbiMarshalVerifier
{
    public static ShaderAbiMarshalVerification Verify<T>(ShaderAbiResourceContract resource)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(resource);
        List<string> errors = [];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            errors.Add($"Managed type '{typeof(T).FullName}' contains references and cannot supply a physical shader ABI.");
        int managedSize = Marshal.SizeOf<T>();
        if (managedSize != resource.ByteSize)
            errors.Add($"Managed size {managedSize} does not match ABI size {resource.ByteSize} for '{resource.Name}'.");

        foreach (ShaderAbiMemberContract member in resource.Members)
        {
            string fieldName = member.CpuFieldName ?? member.PhysicalName;
            try
            {
                var field = typeof(T).GetField(fieldName);
                if (field is null)
                {
                    errors.Add($"Managed type '{typeof(T).FullName}' has no field '{member.ProviderName}'.");
                    continue;
                }
                int offset = checked((int)Marshal.OffsetOf<T>(fieldName));
                if (offset != member.Offset)
                    errors.Add($"Member '{member.ProviderName}' offset {offset} does not match ABI offset {member.Offset}.");
                int fieldSize = Marshal.SizeOf(field.FieldType);
                if (fieldSize != member.Size)
                    errors.Add($"Member '{member.ProviderName}' size {fieldSize} does not match ABI size {member.Size}.");
                if (member.PhysicalType.StartsWith("PhysicalStorageBuffer<", StringComparison.Ordinal) &&
                    (field.FieldType != typeof(ulong) || fieldSize != sizeof(ulong)))
                    errors.Add($"Physical pointer member '{member.ProviderName}' must be a UInt64 field.");
                Type valueType = field.FieldType;
                string physicalType = member.PhysicalType;
                if (member.ArrayCount > 0)
                {
                    var inline = (InlineArrayAttribute?)Attribute.GetCustomAttribute(valueType, typeof(InlineArrayAttribute));
                    var elements = valueType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (inline?.Length != member.ArrayCount || elements.Length != 1 || Marshal.SizeOf(elements[0].FieldType) != member.ArrayStride)
                        errors.Add($"CPU array '{fieldName}' does not match its explicit count/stride.");
                    else
                        valueType = elements[0].FieldType;
                    physicalType = physicalType.EndsWith("[]", StringComparison.Ordinal) ? physicalType[..^2] : physicalType;
                }
                Type? expectedType = physicalType switch
                {
                    "float" => typeof(float), "int" => typeof(int), "uint" => typeof(uint),
                    "float2" => typeof(System.Numerics.Vector2), "float3" => typeof(System.Numerics.Vector3),
                    "float4" => typeof(System.Numerics.Vector4), "float4x4" => typeof(System.Numerics.Matrix4x4),
                    _ when physicalType.StartsWith("PhysicalStorageBuffer<", StringComparison.Ordinal) => typeof(ulong),
                    _ => null,
                };
                if (expectedType is null || valueType != expectedType)
                    errors.Add($"CPU field '{fieldName}' type {valueType} does not match '{physicalType}'.");
                // Matrix4x4 is copied verbatim; no implicit transpose is performed.
                if (physicalType == "float4x4" &&
                    (member.MatrixOrder != ShaderAbiMatrixOrder.RowMajor || member.MatrixStride != 16))
                    errors.Add($"CPU matrix '{fieldName}' requires physical RowMajor storage with stride 16.");
            }
            catch (ArgumentException)
            {
                errors.Add($"Managed type '{typeof(T).FullName}' has no marshalable member '{member.ProviderName}'.");
            }
        }

        return new ShaderAbiMarshalVerification(errors.Count == 0, managedSize, errors);
    }
}
