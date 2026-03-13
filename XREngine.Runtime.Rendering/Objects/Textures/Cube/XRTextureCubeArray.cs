using MemoryPack;
using System;
using System.Linq;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    [MemoryPackable]
    public partial class XRTextureCubeArray : XRTexture
    {
        private XRTextureCube[] _cubes = Array.Empty<XRTextureCube>();
        private bool _resizable = true;
        private ESizedInternalFormat _sizedInternalFormat = ESizedInternalFormat.Rgba8;
        private ETexMagFilter _magFilter = ETexMagFilter.Linear;
        private ETexMinFilter _minFilter = ETexMinFilter.Linear;
        private ETexWrapMode _uWrap = ETexWrapMode.ClampToEdge;
        private ETexWrapMode _vWrap = ETexWrapMode.ClampToEdge;
        private ETexWrapMode _wWrap = ETexWrapMode.ClampToEdge;
        private float _lodBias = 0.0f;

        [MemoryPackConstructor]
        public XRTextureCubeArray()
        {
        }

        public XRTextureCubeArray(params XRTextureCube[] cubes)
        {
            Cubes = cubes ?? Array.Empty<XRTextureCube>();
        }

        public XRTextureCubeArray(uint layerCount, uint extent, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData = false, int mipCount = 1)
        {
            XRTextureCube[] cubes = new XRTextureCube[layerCount];
            for (int i = 0; i < layerCount; ++i)
                cubes[i] = new XRTextureCube(extent, internalFormat, format, type, allocateData, mipCount);
            Cubes = cubes;
        }

        public XRTextureCube[] Cubes
        {
            get => _cubes;
            set => SetField(ref _cubes, value ?? Array.Empty<XRTextureCube>());
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
            get => Cubes.Length > 0 ? Cubes[0].MagFilter : _magFilter;
            set
            {
                _magFilter = value;
                foreach (var cube in Cubes)
                    cube.MagFilter = value;
            }
        }

        public ETexMinFilter MinFilter
        {
            get => Cubes.Length > 0 ? Cubes[0].MinFilter : _minFilter;
            set
            {
                _minFilter = value;
                foreach (var cube in Cubes)
                    cube.MinFilter = value;
            }
        }

        public ETexWrapMode UWrap
        {
            get => Cubes.Length > 0 ? Cubes[0].UWrap : _uWrap;
            set
            {
                _uWrap = value;
                foreach (var cube in Cubes)
                    cube.UWrap = value;
            }
        }

        public ETexWrapMode VWrap
        {
            get => Cubes.Length > 0 ? Cubes[0].VWrap : _vWrap;
            set
            {
                _vWrap = value;
                foreach (var cube in Cubes)
                    cube.VWrap = value;
            }
        }

        public ETexWrapMode WWrap
        {
            get => Cubes.Length > 0 ? Cubes[0].WWrap : _wWrap;
            set
            {
                _wWrap = value;
                foreach (var cube in Cubes)
                    cube.WWrap = value;
            }
        }

        public float LodBias
        {
            get => Cubes.Length > 0 ? Cubes[0].LodBias : _lodBias;
            set
            {
                _lodBias = value;
                foreach (var cube in Cubes)
                    cube.LodBias = value;
            }
        }

        public uint Extent => Cubes.Length > 0 ? Cubes[0].Extent : 0u;

        public uint LayerCount => (uint)Cubes.Length;

        public override uint MaxDimension => Extent;

        public override Vector3 WidthHeightDepth => new(Extent, Extent, Math.Max(1u, LayerCount));

        public override bool HasAlphaChannel => Cubes.Any(c => c.HasAlphaChannel);

        public event Action? Resized;

        public void Resize(uint extent)
        {
            if (!Resizable)
                return;

            foreach (var cube in Cubes)
                cube.Resize(extent);

            Resized?.Invoke();
        }

        private void CubeResized() => Resized?.Invoke();

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change && propName == nameof(Cubes))
                DetachCubeEvents();
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(Cubes))
                AttachCubeEvents();
        }

        private void AttachCubeEvents()
        {
            if (Cubes is null)
                return;

            foreach (var cube in Cubes)
            {
                cube.Resized += CubeResized;
                ApplySharedSettings(cube);
            }
        }

        private void DetachCubeEvents()
        {
            if (Cubes is null)
                return;

            foreach (var cube in Cubes)
                cube.Resized -= CubeResized;
        }

        private void ApplySharedSettings(XRTextureCube cube)
        {
            cube.MagFilter = _magFilter;
            cube.MinFilter = _minFilter;
            cube.UWrap = _uWrap;
            cube.VWrap = _vWrap;
            cube.WWrap = _wWrap;
            cube.LodBias = _lodBias;
        }
    }
}