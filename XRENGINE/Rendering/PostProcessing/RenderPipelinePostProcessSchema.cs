using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering.PostProcessing;

public enum PostProcessParameterKind
{
    Float,
    Int,
    Bool,
    Vector2,
    Vector3,
    Vector4,
}

public sealed class PostProcessEnumOption(string label, int value)
{
    public string Label { get; } = label;
    public int Value { get; } = value;
}

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
    public string Name { get; } = name;
    public string DisplayName { get; } = string.IsNullOrWhiteSpace(displayName) ? name : displayName;
    public PostProcessParameterKind Kind { get; } = kind;
    public bool IsUniform { get; } = isUniform;
    public string? UniformName { get; } = uniformName;
    public object? DefaultValue { get; } = defaultValue;
    public bool IsColor { get; } = isColor;
    public float? Min { get; } = min;
    public float? Max { get; } = max;
    public float? Step { get; } = step;
    public IReadOnlyList<PostProcessEnumOption> EnumOptions { get; } = enumOptions ?? Array.Empty<PostProcessEnumOption>();
    public Func<object, bool>? VisibilityCondition { get; } = visibilityCondition;
}

public sealed class PostProcessStageDescriptor(
    string key,
    string displayName,
    IReadOnlyList<PostProcessParameterDescriptor> parameters,
    Type? backingType)
{
    public string Key { get; } = key;
    public string DisplayName { get; } = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
    public IReadOnlyList<PostProcessParameterDescriptor> Parameters { get; } = parameters ?? Array.Empty<PostProcessParameterDescriptor>();
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type? BackingType { get; } = backingType;
}

public sealed class PostProcessCategoryDescriptor(string key, string displayName, string? description, IReadOnlyList<string> stageKeys)
{
    public string Key { get; } = key;
    public string DisplayName { get; } = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
    public string? Description { get; } = description;
    public IReadOnlyList<string> StageKeys { get; } = stageKeys ?? Array.Empty<string>();
}

public sealed class RenderPipelinePostProcessSchema(
    IReadOnlyDictionary<string, PostProcessStageDescriptor> stages,
    IReadOnlyList<PostProcessCategoryDescriptor> categories)
{
    public static RenderPipelinePostProcessSchema Empty { get; } = new(
        new Dictionary<string, PostProcessStageDescriptor>(StringComparer.Ordinal),
        Array.Empty<PostProcessCategoryDescriptor>());

    public IReadOnlyDictionary<string, PostProcessStageDescriptor> StagesByKey { get; } = stages ?? new Dictionary<string, PostProcessStageDescriptor>(StringComparer.Ordinal);
    public IReadOnlyList<PostProcessCategoryDescriptor> Categories { get; } = categories ?? Array.Empty<PostProcessCategoryDescriptor>();

    public bool TryGetStage(string key, out PostProcessStageDescriptor? descriptor)
        => StagesByKey.TryGetValue(key, out descriptor);

    public bool IsEmpty => StagesByKey.Count == 0;
}
