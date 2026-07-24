using System;
using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_CopyTextureToBuffer : ViewportRenderCommand
    {
        public string? SourceTextureName { get; set; }
        public string? DestinationBufferName { get; set; }
        public int SourceMipLevel { get; set; }
        public int SourceLayerIndex { get; set; }
        public bool PreferGpuReadback { get; set; } = true;
        public bool UploadToGpuBuffer { get; set; } = true;

        public override string GpuProfilingName
            => SourceTextureName is null || DestinationBufferName is null
                ? base.GpuProfilingName
                : $"{base.GpuProfilingName}[{SourceTextureName} mip{SourceMipLevel} layer{SourceLayerIndex}->{DestinationBufferName}]";

        protected override void Execute()
        {
            if (SourceTextureName is null || DestinationBufferName is null)
                return;

            var instance = ActivePipelineInstance;
            if (!instance.TryGetTexture(SourceTextureName, out XRTexture? sourceTexture) || sourceTexture is null)
                return;

            XRDataBuffer? destinationBuffer = instance.GetBuffer(DestinationBufferName);
            if (destinationBuffer is null)
                return;

            if (TryReadTextureBytes(sourceTexture, out byte[] bytes))
            {
                destinationBuffer.SetRawBytes(bytes);
                if (UploadToGpuBuffer)
                    destinationBuffer.PushData();
                return;
            }

            throw new InvalidOperationException($"Texture '{SourceTextureName}' could not be copied into buffer '{DestinationBufferName}'.");
        }

        private bool TryReadTextureBytes(XRTexture texture, out byte[] bytes)
        {
            bytes = [];

            if (!PreferGpuReadback &&
                texture is XRTexture2D source2D &&
                SourceMipLevel >= 0 &&
                SourceMipLevel < source2D.Mipmaps.Length &&
                source2D.Mipmaps[SourceMipLevel].Data is { } cpuData)
            {
                bytes = cpuData.GetBytes();
                return bytes.Length > 0;
            }

            if (AbstractRenderer.Current is IRuntimeRendererHost renderer &&
                renderer.TryGetBackendCapability<IRenderCaptureBackendCapability>(out var capture) &&
                capture is not null)
            {
                IGLTexture? apiTexture = null;
                foreach (IRenderAPIObject wrapper in texture.APIWrappers)
                {
                    if (wrapper is IGLTexture glTexture)
                    {
                        apiTexture = glTexture;
                        break;
                    }
                }

                if (apiTexture is not null &&
                    capture.TryCaptureTextureBytes(apiTexture.BindingId, SourceMipLevel, SourceLayerIndex, out bytes, out _, out _, out _, out _))
                {
                    return true;
                }
            }

            if (texture is XRTexture2D fallback2D &&
                SourceMipLevel >= 0 &&
                SourceMipLevel < fallback2D.Mipmaps.Length &&
                fallback2D.Mipmaps[SourceMipLevel].Data is { } fallbackData)
            {
                bytes = fallbackData.GetBytes();
                return bytes.Length > 0;
            }

            return false;
        }
    }
}
