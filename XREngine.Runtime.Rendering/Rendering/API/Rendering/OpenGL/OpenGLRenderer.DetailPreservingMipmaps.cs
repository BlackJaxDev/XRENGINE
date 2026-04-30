using System;
using System.Collections.Generic;
using System.Globalization;
using Silk.NET.OpenGL;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private const string DetailPreservingMipmapShaderPath = "Compute/Textures/DetailPreservingMipmaps.comp";

    private readonly Dictionary<XRRenderProgram.EImageFormat, XRRenderProgram> _detailPreservingMipmapPrograms = [];
    private bool? _supportsDetailPreservingMipmapCompute;

    internal XRRenderProgram? GetOrCreateDetailPreservingMipmapProgram(XRRenderProgram.EImageFormat imageFormat)
    {
        if (_supportsDetailPreservingMipmapCompute == false)
            return null;

        _supportsDetailPreservingMipmapCompute ??= ComputeSupportsDetailPreservingMipmaps();
        if (_supportsDetailPreservingMipmapCompute != true)
            return null;

        if (_detailPreservingMipmapPrograms.TryGetValue(imageFormat, out XRRenderProgram? existing))
            return existing;

        try
        {
            XRShader template = ShaderHelper.LoadEngineShader(DetailPreservingMipmapShaderPath, EShaderType.Compute);
            string imageFormatToken = ToDetailPreservingMipmapImageFormatToken(imageFormat);
            string source = InjectDetailPreservingMipmapImageFormat(template.Source.Text, imageFormatToken);
            string sourcePath = string.IsNullOrWhiteSpace(template.Source.FilePath)
                ? DetailPreservingMipmapShaderPath
                : template.Source.FilePath;

            XRShader shader = new(EShaderType.Compute, new TextFile(sourcePath) { Text = source });
            XRRenderProgram program = new(true, false, shader)
            {
                Name = $"DetailPreservingMipmaps.{imageFormatToken}"
            };

            _detailPreservingMipmapPrograms[imageFormat] = program;
            return program;
        }
        catch (Exception ex)
        {
            _supportsDetailPreservingMipmapCompute = false;
            Debug.OpenGLWarning($"Failed to initialize detail-preserving compute mipmap shader: {ex.Message}");
            return null;
        }
    }

    private static string InjectDetailPreservingMipmapImageFormat(string templateSource, string imageFormatToken)
    {
        const string versionDirective = "#version";

        if (string.IsNullOrWhiteSpace(templateSource))
            return $"#define DPID_IMAGE_FORMAT {imageFormatToken}";

        int firstContentIndex = 0;
        while (firstContentIndex < templateSource.Length && char.IsWhiteSpace(templateSource[firstContentIndex]))
            firstContentIndex++;

        if (firstContentIndex >= templateSource.Length || !templateSource.AsSpan(firstContentIndex).StartsWith(versionDirective, StringComparison.Ordinal))
            return $"#define DPID_IMAGE_FORMAT {imageFormatToken}{Environment.NewLine}{templateSource}";

        int versionLineEnd = templateSource.IndexOfAny(['\r', '\n'], firstContentIndex);
        if (versionLineEnd < 0)
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{templateSource}{Environment.NewLine}#define DPID_IMAGE_FORMAT {imageFormatToken}");

        int insertIndex = versionLineEnd;
        while (insertIndex < templateSource.Length && (templateSource[insertIndex] == '\r' || templateSource[insertIndex] == '\n'))
            insertIndex++;

        return string.Concat(
            templateSource.AsSpan(0, insertIndex),
            $"#define DPID_IMAGE_FORMAT {imageFormatToken}{Environment.NewLine}",
            templateSource.AsSpan(insertIndex));
    }

    private bool ComputeSupportsDetailPreservingMipmaps()
    {
        try
        {
            int major = Api.GetInteger(GLEnum.MajorVersion);
            int minor = Api.GetInteger(GLEnum.MinorVersion);

            bool hasCompute = (major > 4) || (major == 4 && minor >= 3) || IsExtensionSupported("GL_ARB_compute_shader");
            bool hasImageLoadStore = (major > 4) || (major == 4 && minor >= 2) || IsExtensionSupported("GL_ARB_shader_image_load_store");

            return hasCompute && hasImageLoadStore;
        }
        catch
        {
            return false;
        }
    }

    private static string ToDetailPreservingMipmapImageFormatToken(XRRenderProgram.EImageFormat imageFormat)
        => imageFormat switch
        {
            XRRenderProgram.EImageFormat.R8 => "r8",
            XRRenderProgram.EImageFormat.R16 => "r16",
            XRRenderProgram.EImageFormat.R16F => "r16f",
            XRRenderProgram.EImageFormat.R32F => "r32f",
            XRRenderProgram.EImageFormat.RG8 => "rg8",
            XRRenderProgram.EImageFormat.RG16 => "rg16",
            XRRenderProgram.EImageFormat.RG16F => "rg16f",
            XRRenderProgram.EImageFormat.RG32F => "rg32f",
            XRRenderProgram.EImageFormat.RGB8 => "rgb8",
            XRRenderProgram.EImageFormat.RGB16 => "rgb16",
            XRRenderProgram.EImageFormat.RGB16F => "rgb16f",
            XRRenderProgram.EImageFormat.RGB32F => "rgb32f",
            XRRenderProgram.EImageFormat.RGBA8 => "rgba8",
            XRRenderProgram.EImageFormat.RGBA16 => "rgba16",
            XRRenderProgram.EImageFormat.RGBA16F => "rgba16f",
            XRRenderProgram.EImageFormat.RGBA32F => "rgba32f",
            _ => throw new NotSupportedException($"Image format '{imageFormat}' is not supported by the detail-preserving mipmap shader.")
        };
}