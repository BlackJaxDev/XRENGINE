namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Descriptor publication lifetime, independent of who supplies resource contents.</summary>
public enum ShaderAbiDescriptorLifetime
{
    Globals, Compute, Material, Pass, Frame, View, Object, Instance, RuntimeCallback,
}
