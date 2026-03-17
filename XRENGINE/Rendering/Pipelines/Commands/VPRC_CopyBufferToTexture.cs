using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_CopyBufferToTexture : ViewportRenderCommand
    {
        public string? SourceBufferName { get; set; }
        public string? DestinationTextureName { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        public EPixelInternalFormat? InternalFormatOverride { get; set; }
        public EPixelFormat? PixelFormatOverride { get; set; }
        public EPixelType? PixelTypeOverride { get; set; }
        public bool PushToGpuTexture { get; set; } = true;

        protected override void Execute()
        {
            if (SourceBufferName is null || DestinationTextureName is null)
                return;

            XRDataBuffer? sourceBuffer = ActivePipelineInstance.GetBuffer(SourceBufferName);
            XRTexture2D? destinationTexture = ActivePipelineInstance.GetTexture<XRTexture2D>(DestinationTextureName);
            if (sourceBuffer is null || destinationTexture is null)
                return;

            Mipmap2D? existingMip = destinationTexture.Mipmaps.FirstOrDefault();
            uint width = Width > 0 ? Width : existingMip?.Width ?? destinationTexture.Width;
            uint height = Height > 0 ? Height : existingMip?.Height ?? destinationTexture.Height;
            if (width == 0 || height == 0)
            {
                throw new InvalidOperationException(
                    $"Buffer-to-texture copy for '{DestinationTextureName}' requires explicit Width and Height when the destination texture has no existing mip data.");
            }

            EPixelInternalFormat internalFormat = InternalFormatOverride ?? existingMip?.InternalFormat ?? EPixelInternalFormat.Rgba8;
            EPixelFormat pixelFormat = PixelFormatOverride ?? existingMip?.PixelFormat ?? EPixelFormat.Rgba;
            EPixelType pixelType = PixelTypeOverride ?? existingMip?.PixelType ?? EPixelType.UnsignedByte;

            uint expectedLength = (uint)XRTexture.AllocateBytes(width, height, pixelFormat, pixelType).Length;
            byte[] rawBytes = sourceBuffer.GetRawBytes(expectedLength);

            Mipmap2D mip = new(width, height, internalFormat, pixelFormat, pixelType, allocateData: false)
            {
                Data = new DataSource(rawBytes)
            };

            destinationTexture.Mipmaps = [mip];
            if (PushToGpuTexture)
                destinationTexture.PushData();
        }
    }
}