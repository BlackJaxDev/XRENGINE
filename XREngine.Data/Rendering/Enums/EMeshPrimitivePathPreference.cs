namespace XREngine.Data.Rendering;

/// <summary>
/// Selects how a GPU-submitted mesh pass chooses its primitive-generation path.
/// This is independent from CPU, instrumented GPU-indirect, or production
/// zero-readback submission.
/// </summary>
public enum EMeshPrimitivePathPreference
{
    /// <summary>Submit every draw through the conventional vertex/index path.</summary>
    TraditionalOnly = 0,

    /// <summary>Use task/mesh shaders for eligible draws and plan all others traditionally.</summary>
    MeshShaderPreferred = 1,

    /// <summary>Reject the pass unless every required mesh-shader contract is ready.</summary>
    MeshShaderRequired = 2,
}
