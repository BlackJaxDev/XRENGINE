using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Stable diagnostic and pooling identity for an <see cref="XRRenderProgram"/>.
/// </summary>
public readonly record struct XRRenderProgramDescriptor(
    string StableKey,
    string StageTopology,
    bool Separable,
    string ShaderIdentityKey,
    string GeneratedVertexIdentityKey,
    int RenderSettingsVersion,
    string MaterialVariantKind,
    ulong MaterialVariantHash,
    string VertexLayoutIdentity,
    string TopologyKind,
    int ShaderCount)
{
    public static XRRenderProgramDescriptor Empty { get; } = new(
        string.Empty,
        string.Empty,
        false,
        string.Empty,
        string.Empty,
        0,
        string.Empty,
        0,
        string.Empty,
        string.Empty,
        0);

    public bool IsEmpty => string.IsNullOrWhiteSpace(StableKey);

    public static XRRenderProgramDescriptor FromShaders(
        IEnumerable<XRShader> shaders,
        bool separable,
        int renderSettingsVersion = 0,
        string? generatedVertexIdentity = null,
        string? materialVariantKind = null,
        ulong materialVariantHash = 0,
        string? vertexLayoutIdentity = null,
        string? topologyKind = null)
    {
        ArgumentNullException.ThrowIfNull(shaders);

        List<XRShader> shaderList = [];
        foreach (XRShader? shader in shaders)
        {
            if (shader is not null)
                shaderList.Add(shader);
        }

        string stageTopology = BuildStageTopology(shaderList);
        string shaderIdentityKey = BuildShaderIdentityKey(shaderList);
        string normalizedGeneratedVertexIdentity = NormalizeSegment(generatedVertexIdentity);
        string normalizedVariantKind = NormalizeSegment(materialVariantKind);
        string normalizedVertexLayoutIdentity = NormalizeSegment(vertexLayoutIdentity);
        string normalizedTopologyKind = NormalizeSegment(topologyKind);

        var stableBuilder = new StringBuilder(256);
        stableBuilder.Append("sep=").Append(separable ? '1' : '0');
        stableBuilder.Append("|settings=").Append(renderSettingsVersion.ToString(CultureInfo.InvariantCulture));
        stableBuilder.Append("|stages=").Append(stageTopology);
        stableBuilder.Append("|shaders=").Append(shaderIdentityKey);
        stableBuilder.Append("|generatedVertex=").Append(normalizedGeneratedVertexIdentity);
        stableBuilder.Append("|variantKind=").Append(normalizedVariantKind);
        stableBuilder.Append("|variantHash=").Append(materialVariantHash.ToString("x16", CultureInfo.InvariantCulture));
        stableBuilder.Append("|vertexLayout=").Append(normalizedVertexLayoutIdentity);
        stableBuilder.Append("|topology=").Append(normalizedTopologyKind);

        return new XRRenderProgramDescriptor(
            StableKey: ComputeSha256Hex(stableBuilder.ToString()),
            StageTopology: stageTopology,
            Separable: separable,
            ShaderIdentityKey: shaderIdentityKey,
            GeneratedVertexIdentityKey: normalizedGeneratedVertexIdentity,
            RenderSettingsVersion: renderSettingsVersion,
            MaterialVariantKind: normalizedVariantKind,
            MaterialVariantHash: materialVariantHash,
            VertexLayoutIdentity: normalizedVertexLayoutIdentity,
            TopologyKind: normalizedTopologyKind,
            ShaderCount: shaderList.Count);
    }

    public static string BuildGeneratedSourceIdentity(string? source)
        => string.IsNullOrEmpty(source)
            ? string.Empty
            : string.Concat("inline:", ComputeSha256Hex(source));

    private static string BuildStageTopology(IReadOnlyList<XRShader> shaders)
    {
        if (shaders.Count == 0)
            return "<none>";

        var builder = new StringBuilder(shaders.Count * 12);
        for (int i = 0; i < shaders.Count; i++)
        {
            if (i > 0)
                builder.Append('|');

            builder.Append(shaders[i].Type);
        }

        return builder.ToString();
    }

    private static string BuildShaderIdentityKey(IReadOnlyList<XRShader> shaders)
    {
        if (shaders.Count == 0)
            return "<none>";

        var builder = new StringBuilder(shaders.Count * 96);
        for (int i = 0; i < shaders.Count; i++)
        {
            if (i > 0)
                builder.Append('|');

            XRShader shader = shaders[i];
            string? sourceText = TryResolveShaderSource(shader);
            builder.Append(shader.Type)
                .Append(':')
                .Append(NormalizeSegment(ResolveShaderLabel(shader)))
                .Append(':')
                .Append(sourceText is null ? "<unresolved>" : ComputeSha256Hex(sourceText));
        }

        return ComputeSha256Hex(builder.ToString());
    }

    private static string? TryResolveShaderSource(XRShader shader)
    {
        try
        {
            if (shader.TryGetResolvedSource(out string resolvedSource, logFailures: false))
                return resolvedSource;
        }
        catch
        {
        }

        return shader.Source?.Text;
    }

    private static string ResolveShaderLabel(XRShader shader)
    {
        if (!string.IsNullOrWhiteSpace(shader.Source?.FilePath))
            return shader.Source.FilePath!;
        if (!string.IsNullOrWhiteSpace(shader.FilePath))
            return shader.FilePath!;
        if (!string.IsNullOrWhiteSpace(shader.Name))
            return shader.Name!;

        return shader.Type.ToString();
    }

    private static string NormalizeSegment(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string ComputeSha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
