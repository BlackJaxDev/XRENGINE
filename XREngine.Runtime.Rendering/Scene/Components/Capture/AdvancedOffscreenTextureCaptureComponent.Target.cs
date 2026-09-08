using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Components.Lights;

public abstract partial class AdvancedOffscreenTextureCaptureComponent
{
    /// <summary>Allocates the exact export format; data captures never acquire a color/post target.</summary>
    private void CreateOutputTarget(uint width, uint height)
    {
        ERenderPipelineOffscreenOutput output = OffscreenIntent.Output;
        var (internalFormat, format, type, sizedFormat) = output switch
        {
            ERenderPipelineOffscreenOutput.HdrColor =>
                (EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f),
            ERenderPipelineOffscreenOutput.Depth =>
                (EPixelInternalFormat.Depth32fStencil8, EPixelFormat.DepthStencil, EPixelType.Float32UnsignedInt248Rev, ESizedInternalFormat.Depth32fStencil8),
            ERenderPipelineOffscreenOutput.Visibility =>
                (EPixelInternalFormat.RG32ui, EPixelFormat.RgInteger, EPixelType.UnsignedInt, ESizedInternalFormat.Rg32ui),
            _ => throw new ArgumentOutOfRangeException(nameof(output), output, "Unknown Advanced capture output."),
        };
        bool color = output == ERenderPipelineOffscreenOutput.HdrColor;
        _outputTexture = new XRTexture2D(width, height, internalFormat, format, type, false)
        {
            MinFilter = color ? ETexMinFilter.Linear : ETexMinFilter.Nearest,
            MagFilter = color ? ETexMagFilter.Linear : ETexMagFilter.Nearest,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
            Resizable = false,
            SizedInternalFormat = sizedFormat,
            AutoGenerateMipmaps = false,
            Name = $"{GetType().Name}.Output",
        };
        _canonicalLifetime = new();
        _outputTexture.CanonicalPublicationLifetime = _canonicalLifetime;
        _frameBuffer = new XRFrameBuffer { Name = $"{GetType().Name}.Target" };
        if (color)
        {
            _depthBuffer = new XRRenderBuffer(width, height, ERenderBufferStorage.Depth24Stencil8);
            _frameBuffer.SetRenderTargets(
                (_outputTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (_depthBuffer, EFrameBufferAttachment.DepthStencilAttachment, 0, -1));
            return;
        }

        _depthBuffer = null;
        _frameBuffer.SetRenderTargets((_outputTexture,
            output == ERenderPipelineOffscreenOutput.Depth
                ? EFrameBufferAttachment.DepthStencilAttachment
                : EFrameBufferAttachment.ColorAttachment0, 0, -1));
    }
}
