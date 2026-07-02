using System.Numerics;

namespace XREngine.Rendering.Occlusion
{
    internal sealed class MaskedOcclusionBuffer
    {
        internal const int TileWidth = 8;
        internal const int TileHeight = 4;

        private static readonly uint[] s_xMasks = BuildXMasks();

        private float[] _depths = [];
        private Tile[] _tiles = [];

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int TileColumns { get; private set; }
        public int TileRows { get; private set; }
        public int TilesClosed { get; private set; }

        public void Resize(int requestedWidth, int requestedHeight)
        {
            int width = Math.Clamp(requestedWidth, TileWidth, 4096);
            int height = Math.Clamp(requestedHeight, TileHeight, 4096);
            width = AlignUp(width, TileWidth);
            height = AlignUp(height, TileHeight);

            if (width == Width && height == Height && _depths.Length == width * height)
                return;

            Width = width;
            Height = height;
            TileColumns = width / TileWidth;
            TileRows = height / TileHeight;
            _depths = new float[width * height];
            _tiles = new Tile[TileColumns * TileRows];
        }

        public void Clear()
        {
            Array.Fill(_depths, float.NegativeInfinity);
            Array.Clear(_tiles);
            TilesClosed = 0;
        }

        public bool TryWritePixel(int x, int y, float reciprocalDepth)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || !float.IsFinite(reciprocalDepth))
                return false;

            int pixelIndex = y * Width + x;
            if (reciprocalDepth <= _depths[pixelIndex])
                return false;

            _depths[pixelIndex] = reciprocalDepth;
            UpdateTile(x, y, reciprocalDepth);
            return true;
        }

        public void WritePixelUnchecked(int x, int y, float reciprocalDepth)
        {
            int pixelIndex = y * Width + x;
            if (reciprocalDepth <= _depths[pixelIndex])
                return;

            _depths[pixelIndex] = reciprocalDepth;
            UpdateTile(x, y, reciprocalDepth);
        }

        public bool IsRectOccluded(int minX, int minY, int maxXExclusive, int maxYExclusive, float queryNearestReciprocalDepth)
        {
            if (!float.IsFinite(queryNearestReciprocalDepth) ||
                Width == 0 ||
                Height == 0 ||
                minX < 0 ||
                minY < 0 ||
                maxXExclusive > Width ||
                maxYExclusive > Height ||
                minX >= maxXExclusive ||
                minY >= maxYExclusive)
            {
                return false;
            }

            const float depthEpsilon = 1e-6f;
            int minTileX = minX / TileWidth;
            int minTileY = minY / TileHeight;
            int maxTileX = (maxXExclusive - 1) / TileWidth;
            int maxTileY = (maxYExclusive - 1) / TileHeight;

            for (int tileY = minTileY; tileY <= maxTileY; tileY++)
            {
                int tileOriginY = tileY * TileHeight;
                int localMinY = Math.Max(0, minY - tileOriginY);
                int localMaxYExclusive = Math.Min(TileHeight, maxYExclusive - tileOriginY);

                for (int tileX = minTileX; tileX <= maxTileX; tileX++)
                {
                    int tileOriginX = tileX * TileWidth;
                    int localMinX = Math.Max(0, minX - tileOriginX);
                    int localMaxXExclusive = Math.Min(TileWidth, maxXExclusive - tileOriginX);
                    uint requiredMask = BuildRequiredMask(localMinX, localMinY, localMaxXExclusive, localMaxYExclusive);
                    if (requiredMask == 0u)
                        return false;

                    ref readonly Tile tile = ref _tiles[tileY * TileColumns + tileX];
                    if ((tile.Mask & requiredMask) != requiredMask)
                        return false;

                    if (tile.ZMin0 + depthEpsilon < queryNearestReciprocalDepth)
                        return false;
                }
            }

            return true;
        }

        public CpuSoftwareOcclusionDebugReadback CreateDebugReadback()
        {
            byte[] rgba = new byte[Width * Height * 4];
            float maxDepth = float.NegativeInfinity;
            for (int i = 0; i < _depths.Length; i++)
            {
                float depth = _depths[i];
                if (float.IsFinite(depth) && depth > maxDepth)
                    maxDepth = depth;
            }

            float scale = maxDepth > 0.0f ? 255.0f / maxDepth : 0.0f;
            for (int i = 0; i < _depths.Length; i++)
            {
                float depth = _depths[i];
                byte value = float.IsFinite(depth) && depth > 0.0f
                    ? (byte)Math.Clamp(depth * scale, 0.0f, 255.0f)
                    : (byte)0;
                int dst = i * 4;
                rgba[dst + 0] = value;
                rgba[dst + 1] = value;
                rgba[dst + 2] = value;
                rgba[dst + 3] = 255;
            }

            return new CpuSoftwareOcclusionDebugReadback(Width, Height, rgba);
        }

        private void UpdateTile(int x, int y, float reciprocalDepth)
        {
            int tileX = x / TileWidth;
            int tileY = y / TileHeight;
            int tileIndex = tileY * TileColumns + tileX;
            ref Tile tile = ref _tiles[tileIndex];

            uint bit = 1u << ((y - tileY * TileHeight) * TileWidth + (x - tileX * TileWidth));
            uint previousMask = tile.Mask;
            bool firstPixel = previousMask == 0u;
            tile.Mask = previousMask | bit;
            if (firstPixel)
            {
                tile.ZMin0 = reciprocalDepth;
                tile.ZMin1 = reciprocalDepth;
            }
            else
            {
                tile.ZMin0 = MathF.Min(tile.ZMin0, reciprocalDepth);
                tile.ZMin1 = MathF.Max(tile.ZMin1, reciprocalDepth);
            }

            if (previousMask != uint.MaxValue && tile.Mask == uint.MaxValue)
                TilesClosed++;
        }

        private static uint BuildRequiredMask(int minX, int minY, int maxXExclusive, int maxYExclusive)
        {
            uint mask = 0u;
            uint xMask = s_xMasks[(minX << 4) | maxXExclusive];
            for (int y = minY; y < maxYExclusive; y++)
                mask |= xMask << (y * TileWidth);

            return mask;
        }

        private static uint[] BuildXMasks()
        {
            uint[] masks = new uint[16 * 16];
            for (int min = 0; min <= TileWidth; min++)
            {
                for (int max = min; max <= TileWidth; max++)
                {
                    uint mask = 0u;
                    for (int x = min; x < max; x++)
                        mask |= 1u << x;
                    masks[(min << 4) | max] = mask;
                }
            }

            return masks;
        }

        private static int AlignUp(int value, int alignment)
            => ((value + alignment - 1) / alignment) * alignment;

        private struct Tile
        {
            public uint Mask;
            public float ZMin0;
            public float ZMin1;
        }
    }

    public sealed class CpuSoftwareOcclusionDebugReadback
    {
        public CpuSoftwareOcclusionDebugReadback(int width, int height, byte[] rgba)
        {
            Width = width;
            Height = height;
            Rgba = rgba;
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Rgba { get; }
    }
}
