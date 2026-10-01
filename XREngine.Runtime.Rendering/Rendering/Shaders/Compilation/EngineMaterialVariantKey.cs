using XREngine.Rendering;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Complete selection key for one cooked material shader. Pass and profile identifiers
/// are explicit producer/consumer contracts, not names inferred from authored GLSL.
/// </summary>
public readonly record struct EngineMaterialVariantKey(
    EngineMaterialSemanticIdentity Semantic,
    ShaderCompileTarget Target,
    string Pass,
    string VertexProfile,
    string OutputProfile)
{
    public void Validate()
    {
        Semantic.Validate();
        if (Semantic.Semantic == EngineMaterialSemantic.None)
            throw new ArgumentException("A cooked material variant requires a non-empty engine semantic.");
        if (!Enum.IsDefined(Target))
            throw new ArgumentOutOfRangeException(nameof(Target), Target, "Unsupported shader target.");
        ValidateProfile(Pass, nameof(Pass));
        ValidateProfile(VertexProfile, nameof(VertexProfile));
        ValidateProfile(OutputProfile, nameof(OutputProfile));
    }

    private static void ValidateProfile(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || value[0] is < 'a' or > 'z')
            throw new ArgumentException("A variant selector must be a lowercase, bounded identifier.", parameterName);
        foreach (char character in value)
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.'))
                throw new ArgumentException("A variant selector must be a lowercase, bounded identifier.", parameterName);
    }
}
