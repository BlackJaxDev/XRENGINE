using XREngine.Extensions;
using XREngine.Imaging;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Models.Materials.Textures
{
    public class CubeMipmap : XRBase
    {
        public bool IsCrossMap => Sides.Length == 1;
        /// <summary>
        /// All 6 sides of the cubemap: +X, -X, +Y, -Y, +Z, -Z
        /// </summary>
        public Mipmap2D[] Sides { get; private set; } = new Mipmap2D[6];

        public CubeMipmap() { }
        public CubeMipmap(RuntimeImage cubeCrossBmp, bool isFillerBitmap = false)
        {
            if (isFillerBitmap)
                SetSides(cubeCrossBmp);
            else if (!SetCrossCubeMap(cubeCrossBmp))
                throw new InvalidOperationException("Cubemap cross dimensions are invalid; width/height be a 4:3 or 3:4 ratio.");
        }

        public CubeMipmap(
            Mipmap2D posX, Mipmap2D negX,
            Mipmap2D posY, Mipmap2D negY,
            Mipmap2D posZ, Mipmap2D negZ)
            => Sides = [posX, negX, posY, negY, posZ, negZ];
        
        public CubeMipmap(uint dim, ColorF4? color = null)
            => SetSides(dim, color);
        public CubeMipmap(uint dim, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData)
            => Sides.Fill(i => new Mipmap2D(dim, dim, internalFormat, format, type, allocateData));

        public bool SetEquirectangularMap(RuntimeImage equirectangularImage)
        {
            using RuntimeImage cross = RuntimeImageCodecs.Require().ReprojectEquirectangularToCubeCross(equirectangularImage);
            return SetCrossCubeMap(cross);
        }
        public bool SetCrossCubeMap(RuntimeImage cubeCrossBmp)
        {
            uint w = cubeCrossBmp.Width;
            uint h = cubeCrossBmp.Height;
            if (w == 0 || h == 0)
                return false;
            (uint X, uint Y, uint Width, uint Height)[] crops;

            if (w % 4 == 0 && 
                w / 4 * 3 == h)
            {
                //Cross is on its side.
                //     __
                //  __|__|__ __        +Y
                // |__|__|__|__|   -X, -Z, +X, +Z
                //    |__|             -Y

                uint dim = w / 4;
                crops =
                [
                    (dim * 2, dim, dim, dim), //+X
                    (0, dim, dim, dim), //-X
                    (dim, 0, dim, dim), //+Y
                    (dim, dim * 2, dim, dim), //-Y
                    (dim * 3, dim, dim, dim), //+Z
                    (dim, dim, dim, dim), //-Z
                ];
            }
            else if (
                h % 4 == 0 &&
                h / 4 * 3 == w)
            {
                //Cross is standing up.
                //     __
                //  __|__|__        +Y
                // |__|__|__|   -X, -Z, +X
                //    |__|          -Y
                //    |__|          +Z

                uint dim = h / 4;
                crops =
                [
                    (dim * 2, dim, dim, dim), //+X
                    (0, dim, dim, dim), //-X
                    (dim, 0, dim, dim), //+Y
                    (dim, dim * 2, dim, dim), //-Y
                    (dim, dim * 3, dim, dim), //+Z
                    (dim, dim, dim, dim), //-Z
                ];
            }
            else
                return false;

            Mipmap2D[] sides = new Mipmap2D[crops.Length];
            for (int i = 0; i < crops.Length; i++)
            {
                using RuntimeImage clone = cubeCrossBmp.CopyRegion(crops[i].X, crops[i].Y, crops[i].Width, crops[i].Height);
                sides[i] = new Mipmap2D(clone);
            }
            Sides = sides;

            return true;
        }

        public void SetSides(
            Mipmap2D posX, Mipmap2D negX,
            Mipmap2D posY, Mipmap2D negY,
            Mipmap2D posZ, Mipmap2D negZ)
            => Sides = [posX, negX, posY, negY, posZ, negZ];

        public void SetSides(RuntimeImage bmp)
        {
            for (int i = 0; i < 6; ++i)
                Sides[i] = new Mipmap2D(bmp);
        }
        
        public void SetSides(uint dim, ColorF4? color = null)
        {
            ColorF4 fill = color ?? new ColorF4(0, 0, 0, 0);
            byte[] pixels = new byte[checked((int)((long)dim * dim * 4))];
            byte red = (byte)Math.Clamp((int)Math.Round(fill.R * 255), 0, 255);
            byte green = (byte)Math.Clamp((int)Math.Round(fill.G * 255), 0, 255);
            byte blue = (byte)Math.Clamp((int)Math.Round(fill.B * 255), 0, 255);
            byte alpha = (byte)Math.Clamp((int)Math.Round(fill.A * 255), 0, 255);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = red;
                pixels[i + 1] = green;
                pixels[i + 2] = blue;
                pixels[i + 3] = alpha;
            }
            using RuntimeImage image = new(dim, dim, EPixelFormat.Rgba, EPixelType.UnsignedByte, pixels);
            SetSides(image);
        }

        public void SetSides(uint dim, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData)
            => Sides.Fill(i => new Mipmap2D(dim, dim, internalFormat, format, type, allocateData));

        public void Resize(uint extent)
        {
            foreach (var side in Sides)
                side.Resize(extent, extent);
        }
        public void InterpolativeResize(uint extent, RuntimeImageResizeMode method)
        {
            foreach (var side in Sides)
                side.InterpolativeResize(extent, extent, method);
        }
        public void AdaptiveResize(uint extent)
        {
            foreach (var side in Sides)
                side.AdaptiveResize(extent, extent);
        }
        public async Task ResizeAsync(uint extent)
            => await Task.WhenAll(Sides.Select(x => x.ResizeAsync(extent, extent)));
        public async Task InterpolativeResizeAsync(uint extent, RuntimeImageResizeMode method)
            => await Task.WhenAll(Sides.Select(x => x.InterpolativeResizeAsync(extent, extent, method)));
        public async Task AdaptiveResizeAsync(uint extent)
            => await Task.WhenAll(Sides.Select(x => x.AdaptiveResizeAsync(extent, extent)));
    }
}
