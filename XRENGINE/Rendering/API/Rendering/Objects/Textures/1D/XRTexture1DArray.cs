using System;
using System.Linq;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRTexture1DArray : XRTexture
    {
        private XRTexture1D[] _textures = [];
        private bool _resizable = true;
        private ESizedInternalFormat _sizedInternalFormat = ESizedInternalFormat.Rgba8;
        private ETexMagFilter _magFilter = ETexMagFilter.Linear;
        private ETexMinFilter _minFilter = ETexMinFilter.Linear;
        private ETexWrapMode _uWrap = ETexWrapMode.Repeat;
        private float _lodBias = 0.0f;

        public XRTexture1DArray()
        {
        }

        public XRTexture1DArray(params XRTexture1D[] textures)
        {
            Textures = textures ?? Array.Empty<XRTexture1D>();
        }

        public XRTexture1DArray(uint layerCount, uint width, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData = false)
        {
            XRTexture1D[] textures = new XRTexture1D[layerCount];
            for (int i = 0; i < layerCount; ++i)
                textures[i] = new XRTexture1D(width, internalFormat, format, type, allocateData);
            Textures = textures;
        }

        public XRTexture1D[] Textures
        {
            get => _textures;
            set => SetField(ref _textures, value ?? Array.Empty<XRTexture1D>());
        }

        public override bool IsResizeable => Resizable;

        public bool Resizable
        {
            get => _resizable;
            set => SetField(ref _resizable, value);
        }

        public ESizedInternalFormat SizedInternalFormat
        {
            get => _sizedInternalFormat;
            set => SetField(ref _sizedInternalFormat, value);
        }

        public ETexMagFilter MagFilter
        {
            get => Textures.Length > 0 ? Textures[0].MagFilter : _magFilter;
            set
            {
                _magFilter = value;
                foreach (var texture in Textures)
                    texture.MagFilter = value;
            }
        }

        public ETexMinFilter MinFilter
        {
            get => Textures.Length > 0 ? Textures[0].MinFilter : _minFilter;
            set
            {
                _minFilter = value;
                foreach (var texture in Textures)
                    texture.MinFilter = value;
            }
        }

        public ETexWrapMode UWrap
        {
            get => Textures.Length > 0 ? Textures[0].UWrap : _uWrap;
            set
            {
                _uWrap = value;
                foreach (var texture in Textures)
                    texture.UWrap = value;
            }
        }

        public float LodBias
        {
            get => Textures.Length > 0 ? Textures[0].LodBias : _lodBias;
            set
            {
                _lodBias = value;
                foreach (var texture in Textures)
                    texture.LodBias = value;
            }
        }

        public uint Width => Textures.Length > 0 ? Textures[0].Width : 0u;

        public uint Depth => (uint)Textures.Length;

        public override uint MaxDimension => Width;

        public override Vector3 WidthHeightDepth => new(Width, 1, Depth);

        public override bool HasAlphaChannel
            => Textures.Any(t => t.HasAlphaChannel);

        public event Action? Resized;

        public void Resize(uint width)
        {
            if (!Resizable || Width == width)
                return;

            foreach (var texture in Textures)
                texture.Resize(width);

            Resized?.Invoke();
        }

        private void IndividualTextureResized()
            => Resized?.Invoke();

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change && propName == nameof(Textures))
                DetachTextureEvents();
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(Textures))
                AttachTextureEvents();
        }

        private void AttachTextureEvents()
        {
            if (Textures is null)
                return;

            foreach (var texture in Textures)
            {
                texture.Resized += IndividualTextureResized;
                ApplySharedSettings(texture);
            }
        }

        private void DetachTextureEvents()
        {
            if (Textures is null)
                return;

            foreach (var texture in Textures)
                texture.Resized -= IndividualTextureResized;
        }

        private void ApplySharedSettings(XRTexture1D texture)
        {
            texture.MagFilter = _magFilter;
            texture.MinFilter = _minFilter;
            texture.UWrap = _uWrap;
            texture.LodBias = _lodBias;
        }
    }
}