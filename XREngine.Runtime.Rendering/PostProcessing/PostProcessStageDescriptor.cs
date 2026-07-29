using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering.PostProcessing;

public sealed class PostProcessStageDescriptor(
    string key,
    string displayName,
    IReadOnlyList<PostProcessParameterDescriptor> parameters,
    Type? backingType,
    Func<object>? backingFactory = null)
{
    public string Key { get; } = key;
    public string DisplayName { get; } = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
    public IReadOnlyList<PostProcessParameterDescriptor> Parameters { get; } = parameters ?? Array.Empty<PostProcessParameterDescriptor>();
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type? BackingType { get; } = backingType;
    public Func<object>? BackingFactory { get; } = backingFactory;

    public bool TryCreateBacking(out object? backing)
    {
        if (BackingFactory is not null)
        {
            backing = BackingFactory.Invoke();
            return backing is not null;
        }

        if (BackingType is not null && PostProcessBackingFactoryRegistry.TryCreate(BackingType, out backing))
            return backing is not null;

        backing = null;
        return false;
    }
}
