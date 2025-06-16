using ImageMagick;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;

namespace XREngine.Rendering
{
    [XR3rdPartyExtensions("gif")]
    public class XRTexture2DArray : XRTexture, IFrameBufferAttachement
    {
        private bool _multiSample;
        private XRTexture2D[] _textures = [];
        private bool _resizable = false;
        private ESizedInternalFormat _sizedInternalFormat = ESizedInternalFormat.Rgba8;

        public override Vector3 WidthHeightDepth => new(Width, Height, Depth);

        public XRTexture2DArray(params XRTexture2D[] textures)
        {
            Textures = textures;
        }
        public XRTexture2DArray(uint count, uint width, uint height, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData = false)
        {
            var textures = new XRTexture2D[count];
            for (int i = 0; i < count; i++)
                textures[i] = new XRTexture2D(width, height, internalFormat, format, type, allocateData);
            Textures = textures;
        }

        public override bool IsResizeable => Resizable;

        /// <summary>
        /// If false, calling resize will do nothing.
        /// Useful for repeating textures that must always be a certain size or textures that never need to be dynamically resized during the game.
        /// False by default.
        /// </summary>
        public bool Resizable
        {
            get => _resizable;
            set => SetField(ref _resizable, value);
        }
        public override uint MaxDimension => Math.Max(Width, Height);
        public bool MultiSample
        {
            get => _multiSample;
            set => SetField(ref _multiSample, value);
        }
        public XRTexture2D[] Textures
        {
            get => _textures;
            set => SetField(ref _textures, value);
        }
        public ESizedInternalFormat SizedInternalFormat
        {
            get => _sizedInternalFormat;
            set => SetField(ref _sizedInternalFormat, value);
        }

        public uint Width => Textures.Length > 0 ? Textures[0].Width : 0u;
        public uint Height => Textures.Length > 0 ? Textures[0].Height : 0u;
        public uint Depth => (uint)Textures.Length;

        public Mipmap2D[]? Mipmaps => Textures.Length > 0 ? Textures[0].Mipmaps : null;

        public class OVRMultiView(int offset, uint numViews)
        {
            public uint NumViews = numViews;
            public int Offset = offset;
        }

        private OVRMultiView? _ovrMultiViewParameters;
        public OVRMultiView? OVRMultiViewParameters
        {
            get => _ovrMultiViewParameters;
            set => SetField(ref _ovrMultiViewParameters, value);
        }

        public ETexMinFilter MinFilter
        {
            get => Textures.Length > 0 ? Textures[0].MinFilter : ETexMinFilter.Nearest;
            set
            {
                foreach (XRTexture2D texture in Textures)
                    texture.MinFilter = value;
            }
        }
        public ETexMagFilter MagFilter
        {
            get => Textures.Length > 0 ? Textures[0].MagFilter : ETexMagFilter.Nearest;
            set
            {
                foreach (XRTexture2D texture in Textures)
                    texture.MagFilter = value;
            }
        }
        public ETexWrapMode UWrap
        {
            get => Textures.Length > 0 ? Textures[0].UWrap : ETexWrapMode.ClampToEdge;
            set
            {
                foreach (XRTexture2D texture in Textures)
                    texture.UWrap = value;
            }
        }
        public ETexWrapMode VWrap
        {
            get => Textures.Length > 0 ? Textures[0].VWrap : ETexWrapMode.ClampToEdge;
            set
            {
                foreach (XRTexture2D texture in Textures)
                    texture.VWrap = value;
            }
        }

        public event Action? Resized = null;

        private void IndividualTextureResized()
        {
            Resized?.Invoke();
        }

        public void Resize(uint width, uint height)
        {
            if (Width == width && Height == height)
                return;

            foreach (XRTexture2D texture in Textures)
                texture.Resize(width, height);

            Resized?.Invoke();
        }

        public delegate void DelAttachImageToFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int layer, int mipLevel);
        public delegate void DelDetachImageFromFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int layer, int mipLevel);

        public event DelAttachImageToFBO? AttachImageToFBORequested;
        public event DelDetachImageFromFBO? DetachImageFromFBORequested;

        public void AttachImageToFBO(XRFrameBuffer fbo, int layer, int mipLevel = 0)
        {
            if (FrameBufferAttachment.HasValue)
                AttachImageToFBO(fbo, FrameBufferAttachment.Value, layer, mipLevel);
        }
        public void DetachImageFromFBO(XRFrameBuffer fbo, int layer, int mipLevel = 0)
        {
            if (FrameBufferAttachment.HasValue)
                DetachImageFromFBO(fbo, FrameBufferAttachment.Value, layer, mipLevel);
        }

        public void AttachImageToFBO(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int layer, int mipLevel = 0)
            => AttachImageToFBORequested?.Invoke(fbo, attachment, layer, mipLevel);
        public void DetachImageFromFBO(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int layer, int mipLevel = 0)
            => DetachImageFromFBORequested?.Invoke(fbo, attachment, layer, mipLevel);

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Textures):
                        if (Textures != null)
                        {
                            foreach (XRTexture2D texture in Textures)
                                texture.Resized -= IndividualTextureResized;
                        }
                        break;
                }
            }
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Textures):
                    if (Textures != null)
                    {
                        foreach (XRTexture2D texture in Textures)
                            texture.Resized += IndividualTextureResized;
                    }
                    break;
            }
        }

        public override void Reload(string path)
            => Load3rdParty(path);
        public override bool Load3rdParty(string filePath)
        {
            using MagickImageCollection collection = new(filePath);
            Textures = new XRTexture2D[collection.Count];
            for (int i = 0; i < collection.Count; i++)
                if (collection[i] is MagickImage mi)
                    Textures[i] = new(mi);
            AutoGenerateMipmaps = true;
            return true;
        }

        public delegate void DelAttachToFBO_OVRMultiView(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel, int offset, uint numViews);
        public delegate void DelDetachFromFBO_OVRMultiView(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel, int offset, uint numViews);

        public event DelAttachToFBO_OVRMultiView? AttachToFBORequested_OVRMultiView;
        public event DelDetachFromFBO_OVRMultiView? DetachFromFBORequested_OVRMultiView;

        public void AttachToFBO_OVRMultiView(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int mipLevel, int offset, uint numViews)
            => AttachToFBORequested_OVRMultiView?.Invoke(fbo, attachment, mipLevel, offset, numViews);
        public void DetachFromFBO_OVRMultiView(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int mipLevel, int offset, uint numViews)
            => DetachFromFBORequested_OVRMultiView?.Invoke(fbo, attachment, mipLevel, offset, numViews);

        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// </summary>
        /// <param name="name">The name of the texture.</param>
        /// <param name="width">The texture's width.</param>
        /// <param name="height">The texture's height.</param>
        /// <param name="internalFmt">The internal texture storage format.</param>
        /// <param name="format">The format of the texture's pixels.</param>
        /// <param name="pixelType">How pixels are stored.</param>
        /// <param name="bufAttach">Where to attach to the framebuffer for rendering to.</param>
        /// <returns>A new 2D texture reference.</returns>
        public static XRTexture2DArray CreateFrameBufferTexture(uint count, uint width, uint height,
            EPixelInternalFormat internalFmt, EPixelFormat format, EPixelType type, EFrameBufferAttachment bufAttach)
            => new(count, width, height, internalFmt, format, type, false)
            {
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
                FrameBufferAttachment = bufAttach,
            };
        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="bounds"></param>
        /// <param name="internalFormat"></param>
        /// <param name="format"></param>
        /// <param name="pixelType"></param>
        /// <returns></returns>
        public static XRTexture2DArray CreateFrameBufferTexture(uint count, IVector2 bounds, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type)
            => CreateFrameBufferTexture(count, (uint)bounds.X, (uint)bounds.Y, internalFormat, format, type);
        /// <summary>
        /// Creates a new texture specifically for attaching to a framebuffer.
        /// </summary>
        /// <param name="name">The name of the texture.</param>
        /// <param name="width">The texture's width.</param>
        /// <param name="height">The texture's height.</param>
        /// <param name="internalFmt">The internal texture storage format.</param>
        /// <param name="format">The format of the texture's pixels.</param>
        /// <param name="pixelType">How pixels are stored.</param>
        /// <returns>A new 2D texture reference.</returns>
        public static XRTexture2DArray CreateFrameBufferTexture(uint count, uint width, uint height, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type)
            => new(count, width, height, internalFormat, format, type, false)
            {
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
            };
    }
}