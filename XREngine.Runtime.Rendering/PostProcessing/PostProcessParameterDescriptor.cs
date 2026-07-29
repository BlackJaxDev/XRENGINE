namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Represents a descriptor for a post-processing parameter, providing metadata such as name, display name, kind, default value, and visibility conditions.
/// </summary>
/// <param name="name">The name of the parameter.</param>
/// <param name="displayName">The display name of the parameter.</param>
/// <param name="kind">The kind of the parameter.</param>
/// <param name="isUniform">Indicates whether the parameter is a uniform.</param>
/// <param name="uniformName">The name of the uniform, if applicable.</param>
/// <param name="defaultValue">The default value of the parameter.</param>
/// <param name="isColor">Indicates whether the parameter represents a color.</param>
/// <param name="min">The minimum value of the parameter, if applicable.</param>
/// <param name="max">The maximum value of the parameter, if applicable.</param>
/// <param name="step">The step value of the parameter, if applicable.</param>
/// <param name="enumOptions">The enumeration options for the parameter, if applicable.</param>
/// <param name="visibilityCondition">The condition that determines the visibility of the parameter.</param>
public sealed class PostProcessParameterDescriptor(
    string name,
    string displayName,
    PostProcessParameterKind kind,
    bool isUniform,
    string? uniformName,
    object? defaultValue,
    bool isColor,
    float? min,
    float? max,
    float? step,
    IReadOnlyList<PostProcessEnumOption>? enumOptions,
    Func<object, bool>? visibilityCondition)
{
    /// <summary>
    /// Gets the name of the parameter.
    /// </summary>
    public string Name { get; } = name;
    /// <summary>
    /// Gets the display name of the parameter. If the display name is null or whitespace, it defaults to the parameter's name.
    /// </summary>
    public string DisplayName { get; } = string.IsNullOrWhiteSpace(displayName) ? name : displayName;
    /// <summary>
    /// Gets the kind of the parameter, indicating its type and behavior in the post-processing system.
    /// </summary>
    public PostProcessParameterKind Kind { get; } = kind;
    /// <summary>
    /// Gets a value indicating whether the parameter is a uniform, which affects how it is handled in shaders and rendering.
    /// </summary>
    public bool IsUniform { get; } = isUniform;
    /// <summary>
    /// Gets the name of the uniform associated with the parameter, if applicable. 
    /// This is used for shader uniform binding and may be null if the parameter is not a uniform.
    /// </summary>
    public string? UniformName { get; } = uniformName;
    /// <summary>
    /// Gets the default value of the parameter, which is used when no specific value is provided.
    /// </summary>
    public object? DefaultValue { get; } = defaultValue;
    /// <summary>
    /// Gets a value indicating whether the parameter represents a color, which may affect how it is displayed and processed in the post-processing system.
    /// </summary>
    public bool IsColor { get; } = isColor;
    /// <summary>
    /// Gets the minimum value of the parameter, if applicable. 
    /// This is used for validation and UI representation of the parameter's range.
    /// </summary>
    public float? Min { get; } = min;
    /// <summary>
    /// Gets the maximum value of the parameter, if applicable. 
    /// This is used for validation and UI representation of the parameter's range.
    /// </summary>
    public float? Max { get; } = max;
    /// <summary>
    /// Gets the step value of the parameter, if applicable. 
    /// This is used for incrementing or decrementing the parameter's value in UI controls and may be null if not applicable.
    /// </summary>
    public float? Step { get; } = step;
    /// <summary>
    /// Gets the enumeration options for the parameter, if applicable. 
    /// This is used for parameters that have a predefined set of values, allowing for selection from a list of options. 
    /// If no enumeration options are provided, it defaults to an empty list.
    /// </summary>
    public IReadOnlyList<PostProcessEnumOption> EnumOptions { get; } = enumOptions ?? [];
    /// <summary>
    /// Gets the condition that determines the visibility of the parameter. 
    /// This is a function that takes an object (typically the current state or context) and returns a boolean indicating whether the parameter should be visible.
    /// </summary>
    public Func<object, bool>? VisibilityCondition { get; } = visibilityCondition;
}
