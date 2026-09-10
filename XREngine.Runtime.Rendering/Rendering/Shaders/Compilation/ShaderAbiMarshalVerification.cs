namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Reports managed layout checks against an explicit shader ABI resource contract.
/// </summary>
public sealed record ShaderAbiMarshalVerification(
    bool IsValid,
    int ManagedSize,
    IReadOnlyList<string> Errors);
