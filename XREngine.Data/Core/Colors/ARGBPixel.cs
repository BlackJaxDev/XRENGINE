using XREngine.Extensions;
using System.ComponentModel;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Data.Colors
{
    [Serializable]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ARGBPixel
    {
        private const float ColorFactor = 1.0f / 255.0f;

        [Browsable(false)]
        public Color Color 
        {
            readonly get => this;
            set => this = value;
        }

        public byte A, R, G, B;

        public ARGBPixel(int argb)
        {
            A = (byte)((argb >> 24) & 0xFF);
            R = (byte)((argb >> 16) & 0xFF);
            G = (byte)((argb >> 8) & 0xFF);
            B = (byte)((argb) & 0xFF);
        }
        public ARGBPixel(uint argb)
        {
            A = (byte)((argb >> 24) & 0xFF);
            R = (byte)((argb >> 16) & 0xFF);
            G = (byte)((argb >> 8) & 0xFF);
            B = (byte)((argb) & 0xFF);
        }

        public ARGBPixel(byte a, byte r, byte g, byte b)
        {
            A = a;
            R = r;
            G = g;
            B = b;
        }

        public readonly int DistanceTo(ARGBPixel p)
        {
            int val = A - p.A;
            int dist = val * val;
            val = R - p.R;
            dist += val * val;
            val = G - p.G;
            dist += val * val;
            val = B - p.B;
            return dist + val;
        }

        public readonly float Luminance()
            => (0.299f * R) + (0.587f * G) + (0.114f * B);
        public readonly bool IsGreyscale()
            => R == G && G == B;
        public readonly int Greyscale()
            => (R + G + B) / 3;

        public static implicit operator ARGBPixel(int val) => new(val);
        public static implicit operator int(ARGBPixel p) => *((bint*)&p);
        public static implicit operator ARGBPixel(uint val) => new(val);
        public static implicit operator uint(ARGBPixel p) => *((buint*)&p);
        public static implicit operator ARGBPixel(Color val) => val.ToArgb();
        public static implicit operator Color(ARGBPixel p) => Color.FromArgb(p);
        public static explicit operator Vector3(ARGBPixel p) => new(p.R * ColorFactor, p.G * ColorFactor, p.B * ColorFactor);
        public static implicit operator Vector4(ARGBPixel p) => new(p.R * ColorFactor, p.G * ColorFactor, p.B * ColorFactor, p.A * ColorFactor);
        public static explicit operator ARGBPixel(ColorF4 p) => new(p.A.ToByte(), p.R.ToByte(), p.G.ToByte(), p.B.ToByte());
        public static implicit operator ARGBPixel(RGBAPixel p) => new(p.A, p.R, p.G, p.B);

        public readonly ARGBPixel Min(ARGBPixel p)
            => new(Math.Min(A, p.A), Math.Min(R, p.R), Math.Min(G, p.G), Math.Min(B, p.B));

        public readonly ARGBPixel Max(ARGBPixel p)
            => new(Math.Max(A, p.A), Math.Max(R, p.R), Math.Max(G, p.G), Math.Max(B, p.B));

        public static bool operator ==(ARGBPixel p1, ARGBPixel p2) { return *((uint*)(&p1)) == *((uint*)(&p2)); }
        public static bool operator !=(ARGBPixel p1, ARGBPixel p2) { return *((uint*)(&p1)) != *((uint*)(&p2)); }

        public override readonly string ToString()
            => $"A:{A} R:{R} G:{G} B:{B}";

        public readonly string ToHexString()
            => $"A:{A:X2} R:{R:X2} G:{G:X2} B:{B:X2}";

        public readonly string ToPaddedString()
            => $"A:{A,3} R:{R,3} G:{G,3} B:{B,3}";

        public readonly string ToARGBColorCode()
            => $"{A:X2}{R:X2}{G:X2}{B:X2}";

        public readonly string ToRGBAColorCode()
            => $"{R:X2}{G:X2}{B:X2}{A:X2}";

        public override readonly int GetHashCode()
            => (int)this;
        public override readonly bool Equals(object? obj)
            => obj is ARGBPixel pixel && pixel == this;

        public readonly unsafe ARGBPixel Inverse()
            => new(A, (byte)(255 - R), (byte)(255 - G), (byte)(255 - B));

        public readonly unsafe ARGBPixel Lighten(int amount)
            => new(A, (byte)Math.Min(R + amount, 255), (byte)Math.Min(G + amount, 255), (byte)Math.Min(B + amount, 255));

        public readonly unsafe ARGBPixel Darken(int amount)
            => new(A, (byte)Math.Max(R - amount, 0), (byte)Math.Max(G - amount, 0), (byte)Math.Max(B - amount, 0));
    }
}
