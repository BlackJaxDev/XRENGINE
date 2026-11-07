namespace XREngine.Rendering;

/// <summary>A constructor parameter of a script base pipeline type.</summary>
/// <param name="Name">The parameter name that a script uses, for example <c>stereo</c>.</param>
/// <param name="Type">The value type. A script value converts to this type.</param>
/// <param name="DefaultValue">The value when the script does not give the parameter.</param>
public sealed record RenderPipelineScriptBaseParameter(string Name, Type Type, object? DefaultValue);
