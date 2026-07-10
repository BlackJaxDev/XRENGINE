namespace XREngine;

public readonly record struct RvcFrameGraphResourceDescriptor(
    string Name,
    ERvcFrameGraphResourceScope Scope,
    ERvcFrameGraphResourceLifetime Lifetime,
    ERvcFrameGraphUsage Usage,
    string Format,
    string DependsOn)
{
    public bool IsPerView => Scope == ERvcFrameGraphResourceScope.PerView;
}
