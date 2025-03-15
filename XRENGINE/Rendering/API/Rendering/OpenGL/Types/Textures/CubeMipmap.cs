using Extensions;
using ImageMagick;
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
        public CubeMipmap(MagickImage cubeCrossBmp, bool isFillerBitmap = false)
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
        
        public CubeMipmap(uint dim, MagickColor? color = null)
            => SetSides(dim, color);
        public CubeMipmap(uint dim, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData)
            => Sides.Fill(i => new Mipmap2D(dim, dim, internalFormat, format, type, allocateData));

        public bool SetEquirectangularMap(MagickImage equirectangularBmp)
        {
            //Convert the equirectangular map to a cubemap.

            uint inWidth = equirectangularBmp.Width;
            uint inHeight = equirectangularBmp.Height;
            uint outWidth = inWidth;
            uint outHeight = inWidth * 3 / 4;

            using MagickImage imgOut = new(MagickColors.Black, outWidth, outHeight);
            ConvertBack(equirectangularBmp, imgOut);
            return SetCrossCubeMap(imgOut);

        }
        static void ConvertBack(MagickImage imgIn, MagickImage imgOut)
        {
            uint inWidth = imgIn.Width;
            uint inHeight = imgIn.Height;
            Console.WriteLine("Input image size: {0} x {1}", inWidth, inHeight);
            uint outWidth = imgOut.Width;
            uint outHeight = imgOut.Height;
            Console.WriteLine("Output image size: {0} x {1}", outWidth, outHeight);

            uint edge = inWidth / 4;
            Console.WriteLine("Edge length in pixels: {0}", edge);

            var inPixels = imgIn.GetPixels();
            var outPixels = imgOut.GetPixels();

            double maxValue = (double)Quantum.Max;

            for (uint i = 0; i < outWidth; i++)
            {
                if (i % 100 == 0)
                {
                    Console.WriteLine("Processing {0} of {1}", i, outWidth);
                }
                uint face = i / edge;
                IEnumerable<int> rng;
                if (face == 2)
                {
                    rng = Enumerable.Range(0, (int)edge * 3);
                }
                else
                {
                    rng = Enumerable.Range((int)edge, (int)edge);
                }

                foreach (int j in rng)
                {
                    int face2;
                    if (j < edge)
                    {
                        face2 = 4; // top
                    }
                    else if (j >= 2 * edge)
                    {
                        face2 = 5; // bottom
                    }
                    else
                    {
                        face2 = (int)face;
                    }

                    var xyz = OutImgToXYZ((int)i, j, face2, (int)edge);
                    double x = xyz.Item1;
                    double y = xyz.Item2;
                    double z = xyz.Item3;
                    double theta = Math.Atan2(y, x); // -pi to pi
                    double r = Hypot(x, y);
                    double phi = Math.Atan2(z, r); // -pi/2 to pi/2

                    // Source image coordinates
                    double uf = (2.0 * edge * (theta + Math.PI) / Math.PI);
                    double vf = (2.0 * edge * (Math.PI / 2 - phi) / Math.PI);

                    // Bilinear interpolation
                    int ui = (int)Math.Floor(uf);
                    int vi = (int)Math.Floor(vf);
                    int u2 = ui + 1;
                    int v2 = vi + 1;
                    double mu = uf - ui;
                    double nu = vf - vi;

                    // Get pixel values
                    int ui_mod = ui % (int)inWidth;
                    if (ui_mod < 0) ui_mod += (int)inWidth;
                    int u2_mod = u2 % (int)inWidth;
                    if (u2_mod < 0) u2_mod += (int)inWidth;
                    int vi_clipped = Clip(vi, 0, (int)inHeight - 1);
                    int v2_clipped = Clip(v2, 0, (int)inHeight - 1);

                    var A = inPixels.GetPixel(ui_mod, vi_clipped);
                    var B = inPixels.GetPixel(u2_mod, vi_clipped);
                    var C = inPixels.GetPixel(ui_mod, v2_clipped);
                    var D = inPixels.GetPixel(u2_mod, v2_clipped);

                    // Interpolate
                    double red = A.GetChannel(0) * (1 - mu) * (1 - nu) + B.GetChannel(0) * mu * (1 - nu)
                               + C.GetChannel(0) * (1 - mu) * nu + D.GetChannel(0) * mu * nu;
                    double green = A.GetChannel(1) * (1 - mu) * (1 - nu) + B.GetChannel(1) * mu * (1 - nu)
                                 + C.GetChannel(1) * (1 - mu) * nu + D.GetChannel(1) * mu * nu;
                    double blue = A.GetChannel(2) * (1 - mu) * (1 - nu) + B.GetChannel(2) * mu * (1 - nu)
                                + C.GetChannel(2) * (1 - mu) * nu + D.GetChannel(2) * mu * nu;

                    // Scale to maxValue
                    int ri = (int)Math.Round(red);
                    int gi = (int)Math.Round(green);
                    int bi = (int)Math.Round(blue);
                    ri = Clip(ri, 0, (int)maxValue);
                    gi = Clip(gi, 0, (int)maxValue);
                    bi = Clip(bi, 0, (int)maxValue);

                    outPixels.SetPixel((int)i, j, new float[] { (ushort)ri, (ushort)gi, (ushort)bi });
                }
            }
            outPixels.Dispose();
        }

        static Tuple<double, double, double> OutImgToXYZ(int i, int j, int face, int edge)
        {
            double a = 2.0 * (double)i / edge;
            double b = 2.0 * (double)j / edge;
            double x = 0, y = 0, z = 0;

            switch (face)
            {
                case 0: // back
                    x = -1.0;
                    y = 1.0 - a;
                    z = 3.0 - b;
                    break;
                case 1: // left
                    x = a - 3.0;
                    y = -1.0;
                    z = 3.0 - b;
                    break;
                case 2: // front
                    x = 1.0;
                    y = a - 5.0;
                    z = 3.0 - b;
                    break;
                case 3: // right
                    x = 7.0 - a;
                    y = 1.0;
                    z = 3.0 - b;
                    break;
                case 4: // top
                    x = b - 1.0;
                    y = a - 5.0;
                    z = 1.0;
                    break;
                case 5: // bottom
                    x = 5.0 - b;
                    y = a - 5.0;
                    z = -1.0;
                    break;
            }

            return Tuple.Create(x, y, z);
        }

        static int Clip(int x, int min, int max)
        {
            return Math.Min(Math.Max(x, min), max);
        }

        static double Hypot(double x, double y)
        {
            return Math.Sqrt(x * x + y * y);
        }
        public bool SetCrossCubeMap(MagickImage cubeCrossBmp)
        {
            uint w = cubeCrossBmp.Width;
            uint h = cubeCrossBmp.Height;
            MagickGeometry[] crops;

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
                    new((int)dim * 2, (int)dim, dim, dim), //+X
                    new(0, (int)dim, dim, dim), //-X
                    new((int)dim, 0, dim, dim), //+Y
                    new((int)dim, (int)dim * 2, dim, dim), //-Y
                    new((int)dim * 3, (int)dim, dim, dim), //+Z
                    new((int)dim, (int)dim, dim, dim), //-Z
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
                    new((int)dim * 2, (int)dim, dim, dim), //+X
                    new(0, (int)dim, dim, dim), //-X
                    new((int)dim, 0, dim, dim), //+Y
                    new((int)dim, (int)dim * 2, dim, dim), //-Y
                    new((int)dim, (int)dim * 3, dim, dim), //+Z
                    new((int)dim, (int)dim, dim, dim), //-Z
                ];
            }
            else
                return false;

            Sides = [.. crops.Select(x =>
            {
                var clone = cubeCrossBmp.Clone();
                clone.Crop(x);
                return new Mipmap2D((MagickImage)clone);
            })];

            return true;
        }

        public void SetSides(
            Mipmap2D posX, Mipmap2D negX,
            Mipmap2D posY, Mipmap2D negY,
            Mipmap2D posZ, Mipmap2D negZ)
            => Sides = [posX, negX, posY, negY, posZ, negZ];

        public void SetSides(MagickImage bmp)
        {
            for (int i = 0; i < 6; ++i)
                Sides[i] = new Mipmap2D(bmp);
        }
        
        public void SetSides(uint dim, MagickColor? color = null)
            => Sides.Fill(i => new Mipmap2D(new MagickImage(color ??= new MagickColor(0, 0, 0, 0), dim, dim)));

        public void SetSides(uint dim, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type, bool allocateData)
            => Sides.Fill(i => new Mipmap2D(dim, dim, internalFormat, format, type, allocateData));

        public void Resize(uint extent)
        {
            foreach (var side in Sides)
                side.Resize(extent, extent);
        }
        public void InterpolativeResize(uint extent, PixelInterpolateMethod method)
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
        public async Task InterpolativeResizeAsync(uint extent, PixelInterpolateMethod method)
            => await Task.WhenAll(Sides.Select(x => x.InterpolativeResizeAsync(extent, extent, method)));
        public async Task AdaptiveResizeAsync(uint extent)
            => await Task.WhenAll(Sides.Select(x => x.AdaptiveResizeAsync(extent, extent)));
    }
}
