using XREngine.Core.Files;

namespace XREngine.Rendering;

/// <summary>Captures authored shader inputs for an internal Uber build.</summary>
internal readonly record struct UberShaderSourceStamp(
    XRShader Shader, long Revision, TextFile Source, string? Text, string? Path, string? EffectivePath,
    EShaderType Type, bool Generated, ulong VariantHash)
{
    internal static UberShaderSourceStamp Capture(XRShader shader)
        => new(shader, shader.AuthoredSourceRevision, shader.Source, shader.Source.Text,
            shader.Source.FilePath, UberShaderVariantBuilder.ResolveShaderSourcePathOrName(shader),
            shader.Type, shader.IsGeneratedUberVariant, shader.GeneratedUberVariantHash);

    internal bool IsCurrent => !Shader.IsDestroyed &&
        Shader.AuthoredSourceRevision == Revision && ReferenceEquals(Shader.Source, Source) &&
        string.Equals(Source.Text, Text, StringComparison.Ordinal) &&
        string.Equals(UberShaderVariantBuilder.ResolveShaderSourcePathOrName(Shader), EffectivePath, StringComparison.Ordinal) &&
        string.Equals(Source.FilePath, Path, StringComparison.Ordinal) && Shader.Type == Type &&
        Shader.IsGeneratedUberVariant == Generated && Shader.GeneratedUberVariantHash == VariantHash;
}
