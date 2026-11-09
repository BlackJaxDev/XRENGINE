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
    // Odd while the lock holder changes the observed chain, faces, or generation.
    // A reader without the lock accepts its reads only when the value is even and
    // the same before and after them.
    private long _canonicalObservedSequence;
    // Pass memo: the face check passed in pass _memoEpoch for _memoMipmaps. The
    // writer clears the epoch first and writes it last.
    private ulong _memoEpoch;
    private CubeMipmap[]? _memoMipmaps;
    private ulong _memoGeneration;

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
                BeginObservedWrite();
                _canonicalObservedMipmaps = null;
                _canonicalObservedFaces = [];
                EndObservedWrite();
                PublishWitnessPass(0UL, null, 0UL);
            }
            return false;
        }

        ulong witnessEpoch = XREngine.Rendering.Commands.AdvancedSourceWitnessEpoch.Current;
        CubeMipmap[] mipmaps = Mipmaps;
        // One scene publication checks the same cube for every material that
        // binds it. The face check walks every face of every mip, so it runs
        // once per publication pass while the mip chain is the same object.
        // Pass epochs are unique to one thread, so only this thread writes a memo
        // with this epoch.
        if (witnessEpoch != 0UL && Volatile.Read(ref _memoEpoch) == witnessEpoch)
        {
            CubeMipmap[]? memoMipmaps = Volatile.Read(ref _memoMipmaps);
            ulong memoGeneration = Volatile.Read(ref _memoGeneration);
            if (Volatile.Read(ref _memoEpoch) == witnessEpoch &&
                ReferenceEquals(memoMipmaps, mipmaps))
            {
                generation = memoGeneration;
                return true;
            }
        }

        // An unchanged cube needs no lock: the face check only reads the
        // observed state. Another thread that checks the same cube every frame
        // then does not block this one.
        long sequence = Volatile.Read(ref _canonicalObservedSequence);
        if ((sequence & 1L) == 0L)
        {
            CubeMipmap[]? observed = Volatile.Read(ref _canonicalObservedMipmaps);
            CubeFaceWitness[] observedFaces = Volatile.Read(ref _canonicalObservedFaces);
            ulong observedGeneration = Volatile.Read(ref _canonicalSourceGeneration);
            if (ReferenceEquals(observed, mipmaps) &&
                MatchesCanonicalFaces(mipmaps, observed, observedFaces) &&
                Volatile.Read(ref _canonicalObservedSequence) == sequence)
            {
                if (witnessEpoch != 0UL)
                    PublishWitnessPass(witnessEpoch, mipmaps, observedGeneration);
                generation = observedGeneration;
                return true;
            }
        }

        lock (_canonicalSourceSync)
        {
            mipmaps = Mipmaps;
            if (MatchesCanonicalFaces(mipmaps, _canonicalObservedMipmaps, _canonicalObservedFaces))
            {
                if (witnessEpoch != 0UL)
                    PublishWitnessPass(witnessEpoch, mipmaps, _canonicalSourceGeneration);
                generation = _canonicalSourceGeneration;
                return true;
            }

            PublishWitnessPass(0UL, null, 0UL);

            // A changed mip chain owns new face references. Keep no old data
            // source alive after the chain is replaced.
            BeginObservedWrite();
            try
            {
                _canonicalObservedMipmaps = null;
                _canonicalObservedFaces = [];
                if (!TryCaptureCanonicalFaces(mipmaps, out CubeFaceWitness[] faces))
                    return false;

                if (!MatchesCanonicalFaces(mipmaps, mipmaps, faces))
                    return false;

                _canonicalObservedMipmaps = mipmaps;
                _canonicalObservedFaces = faces;
                unchecked { ++_canonicalSourceGeneration; }
                if (_canonicalSourceGeneration == 0UL)
                    _canonicalSourceGeneration = 1UL;
            }
            finally
            {
                EndObservedWrite();
            }

            if (witnessEpoch != 0UL)
                PublishWitnessPass(witnessEpoch, mipmaps, _canonicalSourceGeneration);
            generation = _canonicalSourceGeneration;
            return true;
        }
    }

    private void BeginObservedWrite()
        => Interlocked.Increment(ref _canonicalObservedSequence);

    private void EndObservedWrite()
        => Interlocked.Increment(ref _canonicalObservedSequence);

    /// <summary>
    /// Records that the face check passed in a pass. An epoch of 0 clears the
    /// memo. A memo is valid only for the rest of its pass, so a change inside
    /// the pass is seen by the next pass.
    /// </summary>
    private void PublishWitnessPass(ulong witnessEpoch, CubeMipmap[]? mipmaps, ulong generation)
    {
        Volatile.Write(ref _memoEpoch, 0UL);
        Volatile.Write(ref _memoMipmaps, mipmaps);
        Volatile.Write(ref _memoGeneration, generation);
        Volatile.Write(ref _memoEpoch, witnessEpoch);
    }

    private static bool MatchesCanonicalFaces(
        CubeMipmap[] mipmaps,
        CubeMipmap[]? observedMipmaps,
        CubeFaceWitness[] observedFaces)
    {
        if (!ReferenceEquals(observedMipmaps, mipmaps) ||
            mipmaps.Length == 0 || observedFaces.Length != mipmaps.Length * 6)
            return false;

        for (int mipIndex = 0; mipIndex < mipmaps.Length; ++mipIndex)
        {
            CubeMipmap? mip = mipmaps[mipIndex];
            if (mip is null || mip.Sides is not { Length: 6 } sides)
                return false;
            for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
            {
                Mipmap2D? side = sides[faceIndex];
                if (side is null || !observedFaces[mipIndex * 6 + faceIndex]
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

