namespace XREngine.Rendering;

/// <summary>
/// Marks a code pipeline type that a render pipeline script can derive from with <c>derive Name(...);</c>. The
/// attribute only names the type for tools and tests. <see cref="RenderPipelineScriptBases"/> holds the factory that
/// creates the type, so loading a script does not use reflection.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RenderPipelineScriptBaseAttribute(string scriptName) : Attribute
{
    /// <summary>Gets the name that a script uses in its <c>derive</c> header.</summary>
    public string ScriptName { get; } = scriptName;
}
