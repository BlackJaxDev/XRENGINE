namespace XREngine.Rendering.Shaders;

/// <summary>
/// Marks a sequential, data-only record whose public fields or primary-constructor
/// properties define a shared CPU and GLSL storage-buffer layout.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class GpuRecordAttribute : Attribute
{
    public GpuRecordAttribute(string glslName, string group = "Advanced")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(glslName);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        GlslName = glslName;
        Group = group;
    }

    public string GlslName { get; }

    public string Group { get; }

    /// <summary>Preserves established PascalCase names in non-access shaders.</summary>
    public bool PreserveMemberCase { get; set; }
}

/// <summary>
/// Allows a Vector3 only when the next scalar field explicitly occupies the
/// fourth std430 component and both fields retain their C# byte offsets.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class GpuPaddedVector3Attribute(string paddingFieldName) : Attribute
{
    public string PaddingFieldName { get; } = paddingFieldName;
}
