using MemoryPack;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    /// <summary>
    /// Texture buffer backed by a data buffer and interpreted with a sized internal format.
    /// </summary>
    [MemoryPackable]
    public partial class XRTextureBuffer : XRTexture
    {
        private XRDataBuffer? _dataBuffer;
        private ESizedInternalFormat _sizedInternalFormat = ESizedInternalFormat.Rgba8;
        private uint _texelCount;

        [MemoryPackConstructor]
        public XRTextureBuffer()
        {
        }

        public XRTextureBuffer(XRDataBuffer dataBuffer, ESizedInternalFormat sizedInternalFormat, uint texelCount)
        {
            DataBuffer = dataBuffer;
            _sizedInternalFormat = sizedInternalFormat;
            _texelCount = texelCount;
        }

        /// <summary>
        /// Underlying data buffer that provides storage for this buffer texture.
        /// </summary>
        public XRDataBuffer? DataBuffer
        {
            get => _dataBuffer;
            set => SetField(ref _dataBuffer, value);
        }

        public ESizedInternalFormat SizedInternalFormat
        {
            get => _sizedInternalFormat;
            set => SetField(ref _sizedInternalFormat, value);
        }

        /// <summary>
        /// Number of texels the buffer exposes. Callers set this based on the format and buffer size.
        /// </summary>
        public uint TexelCount
        {
            get => _texelCount;
            set => SetField(ref _texelCount, value);
        }

        public override uint MaxDimension => TexelCount;

        public override Vector3 WidthHeightDepth => new(TexelCount, 1, 1);
    }
}