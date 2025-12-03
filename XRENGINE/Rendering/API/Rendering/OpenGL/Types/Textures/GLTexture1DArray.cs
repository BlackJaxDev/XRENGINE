using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLTexture1DArray(OpenGLRenderer renderer, XRTexture1DArray data) : GLTexture<XRTexture1DArray>(renderer, data)
    {
        private readonly List<LayerBinding> _layerBindings = new();
        private bool _storageSet;

        public override ETextureTarget TextureTarget => ETextureTarget.Texture1DArray;

        protected override void LinkData()
        {
            base.LinkData();

            Data.Resized += DataResized;
            UpdateLayerBindings();
        }

        protected override void UnlinkData()
        {
            base.UnlinkData();

            Data.Resized -= DataResized;
            ClearLayerBindings();
        }

        protected override void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.DataPropertyChanged(sender, e);
            switch (e.PropertyName)
            {
                case nameof(XRTexture1DArray.Textures):
                    UpdateLayerBindings();
                    break;
            }
        }

        private void UpdateLayerBindings()
        {
            ClearLayerBindings();

            if (Data.Textures is null)
            {
                Invalidate();
                return;
            }

            foreach (var texture in Data.Textures)
            {
                if (texture is null)
                    continue;

                _layerBindings.Add(new LayerBinding(this, texture));
            }

            Invalidate();
        }

        private void ClearLayerBindings()
        {
            foreach (var binding in _layerBindings)
                binding.Dispose();
            _layerBindings.Clear();
        }

        private void DataResized()
        {
            _storageSet = false;
            Invalidate();
        }

        protected internal override void PostGenerated()
        {
            _storageSet = false;
            base.PostGenerated();
        }

        protected internal override void PostDeleted()
        {
            _storageSet = false;
            base.PostDeleted();
        }

        public override unsafe void PushData()
        {
            if (IsPushing)
                return;

            try
            {
                IsPushing = true;
                OnPrePushData(out bool shouldPush, out bool allowPostPushCallback);
                if (!shouldPush)
                {
                    if (allowPostPushCallback)
                        OnPostPushData();
                    return;
                }

                if (_layerBindings.Count == 0)
                {
                    if (allowPostPushCallback)
                        OnPostPushData();
                    return;
                }

                Bind();

                Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                EPixelInternalFormat? internalFormatForce = null;
                if (!Data.Resizable && !_storageSet)
                {
                    Api.TextureStorage2D(BindingId, (uint)Data.SmallestMipmapLevel, ToGLEnum(Data.SizedInternalFormat), Data.Width, Math.Max(1u, Data.Depth));
                    internalFormatForce = ToBaseInternalFormat(Data.SizedInternalFormat);
                    _storageSet = true;
                }

                var glTarget = ToGLEnum(TextureTarget);
                int mipCount = Math.Max(1, _layerBindings.Max(b => b.MipmapCount));
                for (int level = 0; level < mipCount; ++level)
                    PushMipmap(glTarget, level, internalFormatForce);

                SetLodParameters();

                if (Data.AutoGenerateMipmaps)
                    GenerateMipmaps();

                if (allowPostPushCallback)
                    OnPostPushData();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                IsPushing = false;
                Unbind();
            }
        }

        private void SetLodParameters()
        {
            int baseLevel = 0;
            int maxLevel = 1000;
            int minLOD = -1000;
            int maxLOD = 1000;

            Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in baseLevel);
            Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in maxLevel);
            Api.TextureParameterI(BindingId, GLEnum.TextureMinLod, in minLOD);
            Api.TextureParameterI(BindingId, GLEnum.TextureMaxLod, in maxLOD);
        }

        private (GLEnum pixelFormat, GLEnum pixelType, InternalFormat internalFormat) ResolveFormat(int level, EPixelInternalFormat? internalFormatForce)
        {
            foreach (var binding in _layerBindings)
            {
                var mip = binding.GetMipmap(level);
                if (mip is null)
                    continue;

                return (
                    ToGLEnum(mip.PixelFormat),
                    ToGLEnum(mip.PixelType),
                    ToInternalFormat(internalFormatForce ?? mip.InternalFormat));
            }

            var fallbackInternal = ToInternalFormat(internalFormatForce ?? EPixelInternalFormat.Rgba);
            return (GLEnum.Rgba, GLEnum.UnsignedByte, fallbackInternal);
        }

        private unsafe void PushMipmap(GLEnum glTarget, int level, EPixelInternalFormat? internalFormatForce)
        {
            if (!Data.Resizable && !_storageSet)
            {
                Debug.LogWarning("Texture storage not set on non-resizable 1D array, can't push mipmaps.");
                return;
            }

            uint baseWidth = Math.Max(1u, Data.Width >> level);
            uint depth = Math.Max(1u, (uint)_layerBindings.Count);
            var (defaultPixelFormat, defaultPixelType, defaultInternalFormat) = ResolveFormat(level, internalFormatForce);

            if (Data.Resizable)
                Api.TexImage2D(glTarget, level, defaultInternalFormat, baseWidth, depth, 0, defaultPixelFormat, defaultPixelType, null);

            for (int layerIndex = 0; layerIndex < _layerBindings.Count; ++layerIndex)
                UploadLayer(glTarget, level, layerIndex, baseWidth, defaultPixelFormat, defaultPixelType);
        }

        private unsafe void UploadLayer(GLEnum glTarget, int level, int layerIndex, uint fallbackWidth, GLEnum defaultPixelFormat, GLEnum defaultPixelType)
        {
            var mip = _layerBindings[layerIndex].GetMipmap(level);
            if (mip is null)
            {
                if (!Data.Resizable && _storageSet)
                    Api.TexSubImage2D(glTarget, level, 0, layerIndex, fallbackWidth, 1, defaultPixelFormat, defaultPixelType, null);
                return;
            }

            uint width = Math.Max(1u, Math.Min(mip.Width, fallbackWidth));
            var pixelFormat = ToGLEnum(mip.PixelFormat);
            var pixelType = ToGLEnum(mip.PixelType);
            var data = mip.Data;
            var pbo = mip.StreamingPBO;

            if (data is null && pbo is null)
            {
                if (!Data.Resizable && _storageSet)
                    Api.TexSubImage2D(glTarget, level, 0, layerIndex, width, 1, pixelFormat, pixelType, null);
                return;
            }

            if (pbo is not null)
            {
                if (pbo.Target != EBufferTarget.PixelUnpackBuffer)
                    throw new ArgumentException("PBO must be bound to PixelUnpackBuffer for texture uploads.");

                pbo.Bind();
                Api.TexSubImage2D(glTarget, level, 0, layerIndex, width, 1, pixelFormat, pixelType, null);
                pbo.Unbind();
            }
            else
            {
                var ptr = data!.Address.Pointer;
                Api.TexSubImage2D(glTarget, level, 0, layerIndex, width, 1, pixelFormat, pixelType, ptr);
            }
        }

        protected override void SetParameters()
        {
            base.SetParameters();

            Api.TextureParameter(BindingId, GLEnum.TextureLodBias, Data.LodBias);

            int magFilter = (int)ToGLEnum(Data.MagFilter);
            Api.TextureParameterI(BindingId, GLEnum.TextureMagFilter, in magFilter);

            int minFilter = (int)ToGLEnum(Data.MinFilter);
            Api.TextureParameterI(BindingId, GLEnum.TextureMinFilter, in minFilter);

            int uWrap = (int)ToGLEnum(Data.UWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapS, in uWrap);

            int vWrap = (int)GLEnum.ClampToEdge;
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapT, in vWrap);
        }

        private sealed class LayerBinding : IDisposable
        {
            private readonly GLTexture1DArray _owner;
            private readonly XRTexture1D _texture;
            private readonly List<Mipmap1D> _trackedMipmaps = new();

            public LayerBinding(GLTexture1DArray owner, XRTexture1D texture)
            {
                _owner = owner;
                _texture = texture;
                _texture.PropertyChanged += TexturePropertyChanged;
                _texture.Resized += TextureResized;
                UpdateMipmapTracking();
            }

            public int MipmapCount => _texture.Mipmaps?.Length ?? 0;

            public Mipmap1D? GetMipmap(int index)
            {
                if (_texture.Mipmaps is null)
                    return null;

                return index >= 0 && index < _texture.Mipmaps.Length ? _texture.Mipmaps[index] : null;
            }

            private void TextureResized()
                => _owner.Invalidate();

            private void TexturePropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(XRTexture1D.Mipmaps))
                    UpdateMipmapTracking();

                _owner.Invalidate();
            }

            private void UpdateMipmapTracking()
            {
                foreach (var mip in _trackedMipmaps)
                    mip.PropertyChanged -= MipmapPropertyChanged;
                _trackedMipmaps.Clear();

                if (_texture.Mipmaps is null)
                    return;

                foreach (var mip in _texture.Mipmaps)
                {
                    if (mip is null)
                        continue;

                    mip.PropertyChanged += MipmapPropertyChanged;
                    _trackedMipmaps.Add(mip);
                }
            }

            private void MipmapPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
                => _owner.Invalidate();

            public void Dispose()
            {
                foreach (var mip in _trackedMipmaps)
                    mip.PropertyChanged -= MipmapPropertyChanged;
                _trackedMipmaps.Clear();

                _texture.PropertyChanged -= TexturePropertyChanged;
                _texture.Resized -= TextureResized;
            }
        }
    }
}
