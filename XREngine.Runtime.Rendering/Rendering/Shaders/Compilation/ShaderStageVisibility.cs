namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Stages permitted to access a resource in an explicit cooked layout.</summary>
[Flags]
public enum ShaderStageVisibility
{
    None = 0,
    Vertex = 1,
    Fragment = 2,
    Compute = 4,
}
