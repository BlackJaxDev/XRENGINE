using Silk.NET.OpenGL;
using System.Text;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public unsafe partial class OpenGLRenderer :
    IRenderTexturePreviewBackendCapability,
    IRenderBackendDiagnosticsCapability,
    IShaderProgramLinkDiagnosticsBackendCapability
{
    /// <inheritdoc />
    public bool TryGetTexturePreviewHandle(
        XRTexture texture,
        in RenderTexturePreviewOptions options,
        out nint handle,
        out bool requiresVerticalFlip,
        out string? failureReason)
    {
        handle = nint.Zero;
        requiresVerticalFlip = true;
        failureReason = null;

        if (GetOrCreateAPIRenderObject(texture) is not IGLTexture glTexture)
        {
            failureReason = "Texture is not supported by the OpenGL preview path.";
            return false;
        }

        if (options.UploadIfNeeded)
        {
            if (!glTexture.IsGenerated)
                glTexture.Generate();
            if (glTexture.IsInvalidated)
                glTexture.PushData();
        }

        uint binding = glTexture.BindingId;
        if (binding == 0 || binding == GLObjectBase.InvalidBindingId || !RawGL.IsTexture(binding))
        {
            failureReason = "Texture has not been uploaded to the GPU yet.";
            return false;
        }

        if (options.ApplySingleChannelSwizzle && IsSingleChannelPreviewFormat(texture))
            ApplyPreviewChannelSwizzle(binding, RenderTexturePreviewChannel.Luminance);
        else if (options.Channel != RenderTexturePreviewChannel.Rgba)
            ApplyPreviewChannelSwizzle(binding, options.Channel);

        if (options.ForceBaseMipSampling)
            ApplyPreviewSamplingState(binding);

        handle = (nint)binding;
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<RenderBackendDiagnosticError> GetTrackedErrors()
    {
        IReadOnlyList<OpenGLDebugErrorInfo> errors = GetTrackedOpenGLErrors();
        if (errors.Count == 0)
            return Array.Empty<RenderBackendDiagnosticError>();

        var result = new RenderBackendDiagnosticError[errors.Count];
        for (int i = 0; i < errors.Count; i++)
        {
            OpenGLDebugErrorInfo error = errors[i];
            result[i] = new RenderBackendDiagnosticError(
                RendererBackendId.OpenGL,
                error.Id,
                error.Count,
                error.Severity,
                error.Type,
                error.Source,
                error.LastSeenUtc,
                error.Message);
        }

        return result;
    }

    /// <inheritdoc />
    public void ClearTrackedErrors()
        => ClearTrackedOpenGLErrors();

    /// <inheritdoc />
    public void CaptureShaderProgramLinkDiagnostics(List<ShaderProgramLinkDiagnostic> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        foreach (KeyValuePair<GenericRenderObject, AbstractRenderAPIObject> pair in RenderObjectCache)
        {
            if (pair.Key is not XRRenderProgram logicalProgram ||
                pair.Value is not GLRenderProgram backendProgram)
            {
                continue;
            }

            ShaderProgramLinkDiagnosticsSnapshot snapshot = backendProgram.GetLinkDiagnosticsSnapshot();
            string logicalName = !string.IsNullOrWhiteSpace(logicalProgram.Name)
                ? logicalProgram.Name!
                : logicalProgram.GetType().Name;
            string sourceSummary = BuildShaderProgramSourceSummary(logicalProgram);
            string programName = ResolveShaderProgramDisplayName(
                logicalProgram,
                in snapshot,
                logicalName,
                sourceSummary);
            destination.Add(new ShaderProgramLinkDiagnostic(
                logicalName,
                programName,
                ResolveShaderProgramUse(logicalProgram, programName, in snapshot),
                sourceSummary,
                snapshot));
        }
    }

    /// <inheritdoc />
    public void LogShaderProgramLifecycleSummary()
        => ShaderProgramLifecycleDiagnostics.LogSummary(GetProgramBinaryUploadSummary());

    /// <inheritdoc />
    public bool RebuildFontAtlas()
    {
        ForceRebuildImGuiFontAtlas();
        return true;
    }

    private static string ResolveShaderProgramDisplayName(
        XRRenderProgram program,
        in ShaderProgramLinkDiagnosticsSnapshot snapshot,
        string logicalName,
        string sourceSummary)
    {
        if (!string.IsNullOrWhiteSpace(program.Name))
            return program.Name!;

        if (!string.IsNullOrWhiteSpace(snapshot.ProgramName) &&
            !snapshot.ProgramName.StartsWith("<unnamed ", StringComparison.Ordinal))
        {
            return snapshot.ProgramName;
        }

        if (!string.IsNullOrWhiteSpace(sourceSummary) && sourceSummary != "-")
        {
            string topology = string.IsNullOrWhiteSpace(snapshot.ShaderStages)
                ? "Unknown"
                : snapshot.ShaderStages;
            return string.Concat(topology, ":", sourceSummary);
        }

        return string.IsNullOrWhiteSpace(snapshot.ProgramName)
            ? logicalName
            : snapshot.ProgramName;
    }

    private static string ResolveShaderProgramUse(
        XRRenderProgram program,
        string programName,
        in ShaderProgramLinkDiagnosticsSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(program.UsageTag))
            return program.UsageTag!;

        if (programName.StartsWith("MaterialPipelineVariant:", StringComparison.Ordinal))
            return "Material fragment variant";
        if (programName.StartsWith("MaterialPipeline:", StringComparison.Ordinal))
            return "Material fragment pipeline";
        if (programName.StartsWith("SeparatedVertex:", StringComparison.Ordinal))
            return "Mesh vertex pipeline";
        if (programName.StartsWith("Combined:", StringComparison.Ordinal))
            return "Mesh combined program";
        if (snapshot.ShaderStages.Contains("Compute", StringComparison.OrdinalIgnoreCase))
            return "Compute dispatch";
        if (program.Separable &&
            snapshot.ShaderStages.Contains("Fragment", StringComparison.OrdinalIgnoreCase))
        {
            return "Fragment pipeline stage";
        }
        if (snapshot.ShaderStages.Contains("Vertex", StringComparison.OrdinalIgnoreCase))
            return "Vertex/mesh draw stage";
        if (snapshot.ShaderStages.Contains("Fragment", StringComparison.OrdinalIgnoreCase))
            return "Fragment draw stage";

        return "Shader program";
    }

    private static string BuildShaderProgramSourceSummary(XRRenderProgram program)
    {
        StringBuilder builder = new(96);
        foreach (XRShader shader in program.Shaders)
        {
            if (shader is null)
                continue;

            if (builder.Length > 0)
                builder.Append(", ");

            builder
                .Append(shader.Type)
                .Append(':')
                .Append(GetShaderProgramShaderName(shader));
        }

        return builder.Length == 0 ? "-" : builder.ToString();
    }

    private static string GetShaderProgramShaderName(XRShader shader)
    {
        if (!string.IsNullOrWhiteSpace(shader.Name))
            return shader.Name!;
        if (!string.IsNullOrWhiteSpace(shader.FilePath))
            return Path.GetFileName(shader.FilePath);
        if (!string.IsNullOrWhiteSpace(shader.Source?.FilePath))
            return Path.GetFileName(shader.Source.FilePath);
        return shader.GetType().Name;
    }

    private static bool IsSingleChannelPreviewFormat(XRTexture texture)
    {
        ESizedInternalFormat format = texture switch
        {
            XRTexture2D texture2D => texture2D.SizedInternalFormat,
            XRTextureViewBase textureView => textureView.InternalFormat,
            _ => default,
        };

        return format is
            ESizedInternalFormat.R8 or
            ESizedInternalFormat.R8Snorm or
            ESizedInternalFormat.R16 or
            ESizedInternalFormat.R16Snorm or
            ESizedInternalFormat.R16f or
            ESizedInternalFormat.R32f or
            ESizedInternalFormat.R8i or
            ESizedInternalFormat.R8ui or
            ESizedInternalFormat.R16i or
            ESizedInternalFormat.R16ui or
            ESizedInternalFormat.R32i or
            ESizedInternalFormat.R32ui or
            ESizedInternalFormat.DepthComponent16 or
            ESizedInternalFormat.DepthComponent24 or
            ESizedInternalFormat.DepthComponent32f or
            ESizedInternalFormat.Depth24Stencil8 or
            ESizedInternalFormat.Depth32fStencil8;
    }

    private void ApplyPreviewSamplingState(uint binding)
    {
        int linear = (int)GLEnum.Linear;
        int baseLevel = 0;
        int maxLevel = 0;
        int clamp = (int)GLEnum.ClampToEdge;
        int compareMode = (int)GLEnum.None;

        RawGL.TextureParameterI(binding, GLEnum.TextureMinFilter, in linear);
        RawGL.TextureParameterI(binding, GLEnum.TextureMagFilter, in linear);
        RawGL.TextureParameterI(binding, GLEnum.TextureBaseLevel, in baseLevel);
        RawGL.TextureParameterI(binding, GLEnum.TextureMaxLevel, in maxLevel);
        RawGL.TextureParameterI(binding, GLEnum.TextureWrapS, in clamp);
        RawGL.TextureParameterI(binding, GLEnum.TextureWrapT, in clamp);
        RawGL.TextureParameterI(binding, GLEnum.TextureCompareMode, in compareMode);
    }

    private void ApplyPreviewChannelSwizzle(uint binding, RenderTexturePreviewChannel channel)
    {
        int red = (int)GLEnum.Red;
        int green = (int)GLEnum.Green;
        int blue = (int)GLEnum.Blue;
        int alpha = (int)GLEnum.Alpha;

        switch (channel)
        {
            case RenderTexturePreviewChannel.Red:
            case RenderTexturePreviewChannel.Luminance:
                green = blue = red;
                alpha = (int)GLEnum.One;
                break;
            case RenderTexturePreviewChannel.Green:
                red = blue = green;
                alpha = (int)GLEnum.One;
                break;
            case RenderTexturePreviewChannel.Blue:
                red = green = blue;
                alpha = (int)GLEnum.One;
                break;
            case RenderTexturePreviewChannel.Alpha:
                red = green = blue = alpha;
                alpha = (int)GLEnum.One;
                break;
        }

        RawGL.TextureParameterI(binding, GLEnum.TextureSwizzleR, in red);
        RawGL.TextureParameterI(binding, GLEnum.TextureSwizzleG, in green);
        RawGL.TextureParameterI(binding, GLEnum.TextureSwizzleB, in blue);
        RawGL.TextureParameterI(binding, GLEnum.TextureSwizzleA, in alpha);
    }
}
