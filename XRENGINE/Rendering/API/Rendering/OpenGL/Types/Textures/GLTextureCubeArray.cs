using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials.Textures;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLTextureCubeArray(OpenGLRenderer renderer, XRTextureCubeArray data) : GLTexture<XRTextureCubeArray>(renderer, data)
    {
        private readonly List<CubeLayerInfo> _cubeLayers = new();
        private bool _storageSet;

        public override ETextureTarget TextureTarget => ETextureTarget.TextureCubeMapArray;

        protected override void LinkData()
        {
            base.LinkData();

            Data.Resized += DataResized;
            UpdateCubeLayers();
        }

        protected override void UnlinkData()
        {
            base.UnlinkData();

            Data.Resized -= DataResized;
            ClearCubeLayers();
        }

        protected override void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.DataPropertyChanged(sender, e);
            switch (e.PropertyName)
            {
                case nameof(XRTextureCubeArray.Cubes):
                    UpdateCubeLayers();
                    break;
            }
        }

        private void UpdateCubeLayers()
        {
            ClearCubeLayers();

            if (Data.Cubes is null)
            {
                Invalidate();
                return;
            }

            foreach (var cube in Data.Cubes)
            {
                if (cube is null)
                    continue;

                _cubeLayers.Add(new CubeLayerInfo(this, cube));
            }

            Invalidate();
        }

        private void ClearCubeLayers()
        {
            foreach (var layer in _cubeLayers)
                layer.Dispose();
            _cubeLayers.Clear();
        }

        private void DataResized()
        {
            _storageSet = false;
            foreach (var layer in _cubeLayers)
                layer.ResetPushState();
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

                if (_cubeLayers.Count == 0)
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
                    uint depth = Math.Max(1u, Data.LayerCount * 6u);
                    Api.TextureStorage3D(BindingId, (uint)Data.SmallestMipmapLevel, ToGLEnum(Data.SizedInternalFormat), Data.Extent, Data.Extent, depth);
                    internalFormatForce = ToBaseInternalFormat(Data.SizedInternalFormat);
                    _storageSet = true;
                }

                var glTarget = ToGLEnum(TextureTarget);
                int mipCount = Math.Max(1, _cubeLayers.Max(l => l.MipmapCount));
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
            foreach (var layer in _cubeLayers)
            {
                var mip = layer.GetMipmap(level);
                if (mip is null)
                    continue;

                var face = mip.Sides.FirstOrDefault();
                if (face is null)
                    continue;

                return (
                    ToGLEnum(face.PixelFormat),
                    ToGLEnum(face.PixelType),
                    ToInternalFormat(internalFormatForce ?? face.InternalFormat));
            }

            var fallbackInternal = ToInternalFormat(internalFormatForce ?? EPixelInternalFormat.Rgba);
            return (GLEnum.Rgba, GLEnum.UnsignedByte, fallbackInternal);
        }

        private unsafe void PushMipmap(GLEnum glTarget, int level, EPixelInternalFormat? internalFormatForce)
        {
            if (!Data.Resizable && !_storageSet)
            {
                Debug.LogWarning("Texture storage not set on non-resizable cube array, can't push mipmaps.");
                return;
            }

            uint baseExtent = Math.Max(1u, Data.Extent >> level);
            uint depth = Math.Max(1u, Data.LayerCount * 6u);
            var (defaultPixelFormat, defaultPixelType, defaultInternalFormat) = ResolveFormat(level, internalFormatForce);

            if (Data.Resizable)
                Api.TexImage3D(glTarget, level, defaultInternalFormat, baseExtent, baseExtent, depth, 0, defaultPixelFormat, defaultPixelType, null);

            for (int layerIndex = 0; layerIndex < _cubeLayers.Count; ++layerIndex)
            {
                var layer = _cubeLayers[layerIndex];
                var mip = layer.GetMipmap(level);

                if (mip is null)
                {
                    if (!Data.Resizable && _storageSet)
                        ClearFaces(glTarget, level, layerIndex, baseExtent, defaultPixelFormat, defaultPixelType);
                    continue;
                }

                for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
                    UploadFace(glTarget, level, layerIndex, faceIndex, baseExtent, mip.Sides[faceIndex], defaultPixelFormat, defaultPixelType);

                layer.MarkPushed(level);
            }
        }

        private unsafe void ClearFaces(GLEnum glTarget, int level, int layerIndex, uint extent, GLEnum pixelFormat, GLEnum pixelType)
        {
            for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
                Api.TexSubImage3D(glTarget, level, 0, 0, layerIndex * 6 + faceIndex, extent, extent, 1, pixelFormat, pixelType, null);
        }

        private unsafe void UploadFace(GLEnum glTarget, int level, int layerIndex, int faceIndex, uint fallbackExtent, Mipmap2D? face, GLEnum defaultPixelFormat, GLEnum defaultPixelType)
        {
            if (face is null)
            {
                if (!Data.Resizable && _storageSet)
                    Api.TexSubImage3D(glTarget, level, 0, 0, layerIndex * 6 + faceIndex, fallbackExtent, fallbackExtent, 1, defaultPixelFormat, defaultPixelType, null);
                return;
            }

            uint width = Math.Max(1u, Math.Min(face.Width, fallbackExtent));
            uint height = Math.Max(1u, Math.Min(face.Height, fallbackExtent));
            var pixelFormat = ToGLEnum(face.PixelFormat);
            var pixelType = ToGLEnum(face.PixelType);
            var data = face.Data;
            var pbo = face.StreamingPBO;
            int zOffset = layerIndex * 6 + faceIndex;

            if (data is null && pbo is null)
            {
                if (!Data.Resizable && _storageSet)
                    Api.TexSubImage3D(glTarget, level, 0, 0, zOffset, width, height, 1, pixelFormat, pixelType, null);
                return;
            }

            if (pbo is not null)
            {
                if (pbo.Target != EBufferTarget.PixelUnpackBuffer)
                    throw new ArgumentException("PBO must be bound to PixelUnpackBuffer for texture uploads.");

                pbo.Bind();
                Api.TexSubImage3D(glTarget, level, 0, 0, zOffset, width, height, 1, pixelFormat, pixelType, null);
                pbo.Unbind();
            }
            else
            {
                var ptr = data!.Address.Pointer;
                Api.TexSubImage3D(glTarget, level, 0, 0, zOffset, width, height, 1, pixelFormat, pixelType, ptr);
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

            int vWrap = (int)ToGLEnum(Data.VWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapT, in vWrap);

            int wWrap = (int)ToGLEnum(Data.WWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapR, in wWrap);
        }

        private sealed class CubeLayerInfo : IDisposable
        {
            private readonly GLTextureCubeArray _owner;
            private readonly XRTextureCube _cube;
            private readonly List<MipmapInfo> _mipmaps = new();

            public CubeLayerInfo(GLTextureCubeArray owner, XRTextureCube cube)
            {
                _owner = owner;
                _cube = cube;
                _cube.PropertyChanged += CubePropertyChanged;
                _cube.Resized += CubeResized;
                UpdateMipmaps();
            }

            public int MipmapCount => _mipmaps.Count;

            public CubeMipmap? GetMipmap(int index)
                => index >= 0 && index < _mipmaps.Count ? _mipmaps[index].Mipmap : null;

            public void MarkPushed(int index)
            {
                if (index >= 0 && index < _mipmaps.Count)
                    _mipmaps[index].HasPushedResizedData = true;
            }

            public void ResetPushState()
            {
                foreach (var mip in _mipmaps)
                    mip.HasPushedResizedData = false;
            }

            private void CubeResized()
            {
                ResetPushState();
                _owner.Invalidate();
            }

            private void CubePropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(XRTextureCube.Mipmaps))
                    UpdateMipmaps();

                _owner.Invalidate();
            }

            private void UpdateMipmaps()
            {
                foreach (var mip in _mipmaps)
                    mip.Dispose();
                _mipmaps.Clear();

                if (_cube.Mipmaps is null)
                    return;

                foreach (var mip in _cube.Mipmaps)
                {
                    if (mip is null)
                        continue;

                    _mipmaps.Add(new MipmapInfo(_owner, mip));
                }
            }

            public void Dispose()
            {
                foreach (var mip in _mipmaps)
                    mip.Dispose();
                _mipmaps.Clear();

                _cube.PropertyChanged -= CubePropertyChanged;
                _cube.Resized -= CubeResized;
            }
        }

        private sealed class MipmapInfo : IDisposable
        {
            private readonly GLTextureCubeArray _owner;
            private readonly CubeMipmap _mipmap;

            public MipmapInfo(GLTextureCubeArray owner, CubeMipmap mipmap)
            {
                _owner = owner;
                _mipmap = mipmap;

                foreach (var side in _mipmap.Sides)
                {
                    if (side is null)
                        continue;

                    side.PropertyChanged += SidePropertyChanged;
                }
            }

            public CubeMipmap Mipmap => _mipmap;

            public bool HasPushedResizedData { get; set; }

            private void SidePropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                switch (e.PropertyName)
                {
                    case nameof(Mipmap2D.Data):
                    case nameof(Mipmap2D.StreamingPBO):
                        _owner.Invalidate();
                        break;
                    case nameof(Mipmap2D.Width):
                    case nameof(Mipmap2D.Height):
                    case nameof(Mipmap2D.InternalFormat):
                    case nameof(Mipmap2D.PixelFormat):
                    case nameof(Mipmap2D.PixelType):
                        HasPushedResizedData = false;
                        _owner.Invalidate();
                        break;
                }
            }

            public void Dispose()
            {
                foreach (var side in _mipmap.Sides)
                {
                    if (side is null)
                        continue;

                    side.PropertyChanged -= SidePropertyChanged;
                }
            }
        }
    }
}
