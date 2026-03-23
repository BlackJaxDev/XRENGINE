using System;

namespace XREngine.Rendering.Pipelines.Commands;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RenderPipelineScriptCommandAttribute(string? methodName = null) : Attribute
{
    public string? MethodName { get; } = methodName;
    public bool IncludeLegacyTypeNameAlias { get; init; } = true;
}
