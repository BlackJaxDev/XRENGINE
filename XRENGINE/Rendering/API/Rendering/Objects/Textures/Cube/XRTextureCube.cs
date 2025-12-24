using MemoryPack;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering
{
    [MemoryPackable]
    public partial class XRTextureCube : XRTexture, IFrameBufferAttachement
    {
        public delegate void DelAttachFaceToFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, ECubemapFace face, int mipLevel);
        public delegate void DelDetachFaceFromFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, ECubemapFace face, int mipLevel);

        public event DelAttachFaceToFBO? AttachFaceToFBORequested;
        public event DelDetachFaceFromFBO? DetachFaceFromFBORequested;

        public override Vector3 WidthHeightDepth => new(Extent, Extent, 6);

        [MemoryPackConstructor]
        public XRTextureCube()
            : this(1u) { }

        public XRTextureCube(
            uint dim,
            int mipCount = 1)
        {
            uint sDim = dim;
            CubeMipmap[] mips = new CubeMipmap[mipCount];
            for (uint i = 0u; i < mipCount; i++)
            {
                mips[i] = new CubeMipmap(sDim);
                sDim >>= 1;
            }
            Mipmaps = mips;
        }

        public XRTextureCube(
            uint dim,
            EPixelInternalFormat internalFormat,
            EPixelFormat pixelFormat,
            EPixelType pixelType,
            bool allocateData,
            int mipCount = 1)
        {
            uint sDim = dim;
            CubeMipmap[] mips = new CubeMipmap[mipCount];
            for (uint i = 0u; i < mipCount; i++)
            {
                mips[i] = new CubeMipmap(sDim, internalFormat, pixelFormat, pixelType, allocateData);
                sDim >>= 1;
            }
            Mipmaps = mips;
        }

        public XRTextureCube(params CubeMipmap[] mipmaps)
            => Mipmaps = mipmaps;

        public CubeMipmap[] _mipmaps = [];
        public CubeMipmap[] Mipmaps
        {
            get => _mipmaps;
            set => SetField(ref _mipmaps, value);
        }

        /// <summary>
        /// How long the cube's sides are.
        /// </summary>
        public uint Extent 
            => Mipmaps is not null && Mipmaps.Length > 0 
            ? Mipmaps[0].Sides[0].Width
            : 0u;

        public override uint MaxDimension => Extent;

        private ETexWrapMode _uWrapMode = ETexWrapMode.ClampToEdge;
        private ETexWrapMode _vWrapMode = ETexWrapMode.ClampToEdge;
        private ETexWrapMode _wWrapMode = ETexWrapMode.ClampToEdge;
        private ETexMinFilter _minFilter = ETexMinFilter.Nearest;
        private ETexMagFilter _magFilter = ETexMagFilter.Nearest;
        private float _lodBias = 0.0f;
        private bool _resizable = true;

        public bool Resizable
        {
            get => _resizable;
            set => SetField(ref _resizable, value);
        }

        public ETexMagFilter MagFilter
        {
            get => _magFilter;
            set => SetField(ref _magFilter, value);
        }

        public ETexMinFilter MinFilter
        {
            get => _minFilter;
            set => SetField(ref _minFilter, value);
        }

        public ETexWrapMode UWrap
        {
            get => _uWrapMode;
            set => SetField(ref _uWrapMode, value);
        }

        public ETexWrapMode VWrap
        {
            get => _vWrapMode;
            set => SetField(ref _vWrapMode, value);
        }

        public ETexWrapMode WWrap
        {
            get => _wWrapMode;
            set => SetField(ref _wWrapMode, value);
        }

        public float LodBias
        {
            get => _lodBias;
            set => SetField(ref _lodBias, value);
        }

        private ESizedInternalFormat _sizedInternalFormat;
        public ESizedInternalFormat SizedInternalFormat
        {
            get => _sizedInternalFormat;
            set => SetField(ref _sizedInternalFormat, value);
        }
        uint IFrameBufferAttachement.Width => Extent;
        uint IFrameBufferAttachement.Height => Extent;

        public void AttachFaceToFBO(XRFrameBuffer fbo, ECubemapFace face, int mipLevel = 0)
        {
            if (FrameBufferAttachment.HasValue)
                AttachFaceToFBO(fbo, FrameBufferAttachment.Value, face, mipLevel);
        }
        public void DetachFaceFromFBO(XRFrameBuffer fbo, ECubemapFace face, int mipLevel = 0)
        {
            if (FrameBufferAttachment.HasValue)
                DetachFaceFromFBO(fbo, FrameBufferAttachment.Value, face, mipLevel);
        }

        public void AttachFaceToFBO(XRFrameBuffer fbo, EFrameBufferAttachment attachment, ECubemapFace face, int mipLevel = 0)
            => AttachFaceToFBORequested?.Invoke(fbo, attachment, face, mipLevel);
        public void DetachFaceFromFBO(XRFrameBuffer fbo, EFrameBufferAttachment attachment, ECubemapFace face, int mipLevel = 0)
            => DetachFaceFromFBORequested?.Invoke(fbo, attachment, face, mipLevel);

        /// <summary>
        /// Resizes the textures stored in memory.
        /// Does nothing if Resizeable is false.
        /// </summary>
        public void Resize(uint extent)
        {
            if (Extent == extent || Mipmaps is null || Mipmaps.Length <= 0)
                return;

            for (int i = 0; i < Mipmaps.Length && extent > 0u; ++i)
            {
                if (Mipmaps[i] is null)
                    continue;

                Mipmaps[i]?.Resize(extent);

                extent >>= 1;
            }

            Resized?.Invoke();
        }

        public event Action? Resized;
    }
}
