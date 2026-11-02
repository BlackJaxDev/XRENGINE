using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering;

public partial class XRTextureCube
{
    private readonly object _canonicalSourceSync = new();
    private CubeMipmap[]? _canonicalObservedMipmaps;
    private CubeFaceWitness[] _canonicalObservedFaces = [];
    private ulong _canonicalSourceGeneration;

    /// <summary>
    /// Gets a revision for a CPU-backed cube. A caller must invalidate a side
    /// after a direct write to its data source.
    /// </summary>
    internal bool TryGetCanonicalCpuSourceGeneration(out ulong generation)
    {
        generation = 0UL;
        if (IsDestroyed || RequiresStorageUsage || FrameBufferAttachment.HasValue)
        {
            lock (_canonicalSourceSync)
            {
                _canonicalObservedMipmaps = null;
                _canonicalObservedFaces = [];
            }
            return false;
        }

        lock (_canonicalSourceSync)
        {
            CubeMipmap[] mipmaps = Mipmaps;
            if (MatchesCanonicalFaces(mipmaps))
            {
                generation = _canonicalSourceGeneration;
                return true;
            }

            // A changed mip chain owns new face references. Keep no old data
            // source alive after the chain is replaced.
            _canonicalObservedMipmaps = null;
            _canonicalObservedFaces = [];
            if (!TryCaptureCanonicalFaces(mipmaps, out CubeFaceWitness[] faces))
                return false;

            _canonicalObservedMipmaps = mipmaps;
            _canonicalObservedFaces = faces;
            if (!MatchesCanonicalFaces(mipmaps))
            {
                _canonicalObservedMipmaps = null;
                _canonicalObservedFaces = [];
                return false;
            }

            unchecked { ++_canonicalSourceGeneration; }
            if (_canonicalSourceGeneration == 0UL)
                _canonicalSourceGeneration = 1UL;
            generation = _canonicalSourceGeneration;
            return true;
        }
    }

    private bool MatchesCanonicalFaces(CubeMipmap[] mipmaps)
    {
        if (!ReferenceEquals(_canonicalObservedMipmaps, mipmaps) ||
            mipmaps.Length == 0 || _canonicalObservedFaces.Length != mipmaps.Length * 6)
            return false;

        for (int mipIndex = 0; mipIndex < mipmaps.Length; ++mipIndex)
        {
            CubeMipmap? mip = mipmaps[mipIndex];
            if (mip is null || mip.Sides is not { Length: 6 } sides)
                return false;
            for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
            {
                Mipmap2D? side = sides[faceIndex];
                if (side is null || !_canonicalObservedFaces[mipIndex * 6 + faceIndex]
                        .Matches(mip, sides, side))
                    return false;
            }
        }
        return true;
    }

    private static bool TryCaptureCanonicalFaces(
        CubeMipmap[] mipmaps,
        out CubeFaceWitness[] faces)
    {
        faces = [];
        if (mipmaps.Length == 0 || mipmaps.Length > int.MaxValue / 6)
            return false;
        for (int mipIndex = 0; mipIndex < mipmaps.Length; ++mipIndex)
        {
            CubeMipmap? mip = mipmaps[mipIndex];
            if (mip is null || mip.Sides is not { Length: 6 } sides)
                return false;
            for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
            {
                Mipmap2D? side = sides[faceIndex];
                if (side is null || side.Data is not { IsDisposed: false, Length: > 0 } ||
                    side.Width == 0u || side.Height == 0u ||
                    side.CanonicalContentWriteActive)
                    return false;
            }
        }

        faces = new CubeFaceWitness[mipmaps.Length * 6];
        for (int mipIndex = 0; mipIndex < mipmaps.Length; ++mipIndex)
        {
            CubeMipmap? mip = mipmaps[mipIndex];
            if (mip is null || mip.Sides is not { Length: 6 } sides)
            {
                faces = [];
                return false;
            }
            for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
            {
                if (sides[faceIndex] is not { } side)
                {
                    faces = [];
                    return false;
                }
                faces[mipIndex * 6 + faceIndex] = new(mip, sides, side);
            }
        }
        return true;
    }

    private readonly struct CubeFaceWitness(CubeMipmap mip, Mipmap2D[] sides, Mipmap2D side)
    {
        private readonly CubeMipmap _mip = mip;
        private readonly Mipmap2D[] _sides = sides;
        private readonly Mipmap2D _side = side;
        private readonly DataSource? _data = side.Data;
        private readonly uint _dataLength = side.Data?.Length ?? 0u;
        private readonly int _epoch = side.CanonicalContentEpoch;
        private readonly uint _width = side.Width;
        private readonly uint _height = side.Height;
        private readonly EPixelInternalFormat _internalFormat = side.InternalFormat;
        private readonly EPixelFormat _pixelFormat = side.PixelFormat;
        private readonly EPixelType _pixelType = side.PixelType;

        public bool Matches(CubeMipmap mip, Mipmap2D[] sides, Mipmap2D side)
        {
            DataSource? data = _data;
            int epoch = side.CanonicalContentEpoch;
            if (!ReferenceEquals(_mip, mip) || !ReferenceEquals(_sides, sides) ||
                !ReferenceEquals(_side, side) || data is not { IsDisposed: false, Length: > 0 } ||
                data.Length != _dataLength || !ReferenceEquals(data, side.Data) ||
                side.CanonicalContentWriteActive || _epoch != epoch || _width != side.Width ||
                _height != side.Height || _internalFormat != side.InternalFormat ||
                _pixelFormat != side.PixelFormat || _pixelType != side.PixelType)
                return false;

            System.Threading.Thread.MemoryBarrier();
            return epoch == side.CanonicalContentEpoch &&
                !side.CanonicalContentWriteActive && !data.IsDisposed &&
                ReferenceEquals(data, side.Data);
        }
    }

}

