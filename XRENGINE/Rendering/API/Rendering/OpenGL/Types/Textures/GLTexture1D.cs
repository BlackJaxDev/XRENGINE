using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLTexture1D(OpenGLRenderer renderer, XRTexture1D data) : GLTexture<XRTexture1D>(renderer, data)
    {
        private readonly List<Mipmap1D> _trackedMipmaps = new();
        private bool _storageSet;

        public override ETextureTarget TextureTarget => ETextureTarget.Texture1D;

        protected override void LinkData()
        {
            base.LinkData();

            Data.Resized += DataResized;
            UpdateTrackedMipmaps();
        }

        protected override void UnlinkData()
        {
            base.UnlinkData();

            Data.Resized -= DataResized;
            ClearTrackedMipmaps();
        }

        protected override void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.DataPropertyChanged(sender, e);
            switch (e.PropertyName)
            {
                case nameof(XRTexture1D.Mipmaps):
                    UpdateTrackedMipmaps();
                    break;
            }
        }

        private void UpdateTrackedMipmaps()
        {
            ClearTrackedMipmaps();

            if (Data.Mipmaps is null)
            {
                Invalidate();
                return;
            }

            foreach (var mip in Data.Mipmaps)
            {
                if (mip is null)
                    continue;

                _trackedMipmaps.Add(mip);
                mip.PropertyChanged += MipmapPropertyChanged;
            }

            Invalidate();
        }

        private void ClearTrackedMipmaps()
        {
            foreach (var mip in _trackedMipmaps)
                mip.PropertyChanged -= MipmapPropertyChanged;
            _trackedMipmaps.Clear();
        }

        private void MipmapPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            => Invalidate();

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

                Bind();

                Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                EPixelInternalFormat? internalFormatForce = null;
                if (!Data.Resizable && !_storageSet)
                {
                    Api.TextureStorage1D(BindingId, (uint)Data.SmallestMipmapLevel, ToGLEnum(Data.SizedInternalFormat), Data.Width);
                    internalFormatForce = ToBaseInternalFormat(Data.SizedInternalFormat);
                    _storageSet = true;
                }

                var glTarget = ToGLEnum(TextureTarget);
                if (Data.Mipmaps is null || Data.Mipmaps.Length == 0)
                    PushMipmap(glTarget, 0, null, internalFormatForce);
                else
                {
                    for (int level = 0; level < Data.Mipmaps.Length; ++level)
                        PushMipmap(glTarget, level, Data.Mipmaps[level], internalFormatForce);
                }

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

        private unsafe void PushMipmap(GLEnum glTarget, int level, Mipmap1D? mip, EPixelInternalFormat? internalFormatForce)
        {
            if (!Data.Resizable && !_storageSet)
            {
                Debug.LogWarning("Texture storage not set on non-resizable 1D texture, can't push mipmaps.");
                return;
            }

            GLEnum pixelFormat;
            GLEnum pixelType;
            InternalFormat internalPixelFormat;
            DataSource? data = null;
            XRDataBuffer? pbo = null;
            uint width;

            if (mip is null)
            {
                internalPixelFormat = ToInternalFormat(internalFormatForce ?? EPixelInternalFormat.Rgba);
                pixelFormat = GLEnum.Rgba;
                pixelType = GLEnum.UnsignedByte;
                width = Math.Max(1u, Data.Width >> level);
            }
            else
            {
                pixelFormat = ToGLEnum(mip.PixelFormat);
                pixelType = ToGLEnum(mip.PixelType);
                internalPixelFormat = ToInternalFormat(internalFormatForce ?? mip.InternalFormat);
                data = mip.Data;
                pbo = mip.StreamingPBO;
                width = Math.Max(1u, mip.Width);
            }

            if (data is null && pbo is null)
            {
                if (!Data.Resizable && _storageSet)
                    Api.TexSubImage1D(glTarget, level, 0, width, pixelFormat, pixelType, null);
                else if (Data.Resizable)
                    Api.TexImage1D(glTarget, level, internalPixelFormat, width, 0, pixelFormat, pixelType, null);
                return;
            }

            if (pbo is not null)
            {
                if (pbo.Target != EBufferTarget.PixelUnpackBuffer)
                    throw new ArgumentException("PBO must be bound to PixelUnpackBuffer for texture uploads.");

                pbo.Bind();
                if (Data.Resizable)
                    Api.TexImage1D(glTarget, level, internalPixelFormat, width, 0, pixelFormat, pixelType, null);
                else
                    Api.TexSubImage1D(glTarget, level, 0, width, pixelFormat, pixelType, null);
                pbo.Unbind();
            }
            else
            {
                var ptr = data!.Address.Pointer;
                if (Data.Resizable)
                    Api.TexImage1D(glTarget, level, internalPixelFormat, width, 0, pixelFormat, pixelType, ptr);
                else
                    Api.TexSubImage1D(glTarget, level, 0, width, pixelFormat, pixelType, ptr);
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
        }
    }
}
