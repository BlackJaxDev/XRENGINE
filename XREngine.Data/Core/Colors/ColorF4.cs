using Extensions;
using MemoryPack;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using YamlDotNet.Serialization;

namespace XREngine.Data.Colors
{
    [Serializable]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ColorF4
    {
        public static readonly ColorF4 White = new(1.0f, 1.0f);
        public static readonly ColorF4 Gray = new(0.5f, 1.0f);
        public static readonly ColorF4 Black = new(0.0f, 1.0f);
        public static readonly ColorF4 Red = new(1.0f, 0.0f, 0.0f);
        public static readonly ColorF4 Green = new(0.0f, 1.0f, 0.0f);
        public static readonly ColorF4 Blue = new(0.0f, 0.0f, 1.0f);
        public static readonly ColorF4 Yellow = new(1.0f, 1.0f, 0.0f);
        public static readonly ColorF4 Cyan = new(0.0f, 1.0f, 1.0f);
        public static readonly ColorF4 Magenta = new(1.0f, 0.0f, 1.0f);
        public static readonly ColorF4 Orange = new(1.0f, 0.5f, 0.0f);
        public static readonly ColorF4 LightOrange = new(1.0f, 0.75f, 0.5f);
        public static readonly ColorF4 DarkOrange = new(0.5f, 0.25f, 0.0f);
        public static readonly ColorF4 Transparent = new(0.0f, 0.0f, 0.0f, 0.0f);
        public static readonly ColorF4 LightGray = new(0.75f, 1.0f);
        public static readonly ColorF4 DarkGray = new(0.25f, 1.0f);
        public static readonly ColorF4 LightRed = new(1.0f, 0.5f, 0.5f);
        public static readonly ColorF4 DarkRed = new(0.5f, 0.0f, 0.0f);
        public static readonly ColorF4 LightGreen = new(0.5f, 1.0f, 0.5f);
        public static readonly ColorF4 DarkGreen = new(0.0f, 0.5f, 0.0f);
        public static readonly ColorF4 LightBlue = new(0.5f, 0.5f, 1.0f);
        public static readonly ColorF4 DarkBlue = new(0.0f, 0.0f, 0.5f);
        public static readonly ColorF4 LightYellow = new(1.0f, 1.0f, 0.5f);
        public static readonly ColorF4 DarkYellow = new(0.5f, 0.5f, 0.0f);
        public static readonly ColorF4 LightCyan = new(0.5f, 1.0f, 1.0f);
        public static readonly ColorF4 DarkCyan = new(0.0f, 0.5f, 0.5f);
        public static readonly ColorF4 LightMagenta = new(1.0f, 0.5f, 1.0f);
        public static readonly ColorF4 DarkMagenta = new(0.5f, 0.0f, 0.5f);
        public static readonly ColorF4 LightPurple = new(0.75f, 0.5f, 1.0f);
        public static readonly ColorF4 DarkPurple = new(0.25f, 0.0f, 0.5f);
        public static readonly ColorF4 LightBrown = new(0.75f, 0.5f, 0.25f);
        public static readonly ColorF4 DarkBrown = new(0.25f, 0.0f, 0.0f);
        public static readonly ColorF4 LightPink = new(1.0f, 0.5f, 0.75f);
        public static readonly ColorF4 DarkPink = new(0.5f, 0.0f, 0.25f);
        public static readonly ColorF4 LightTeal = new(0.5f, 1.0f, 0.75f);
        public static readonly ColorF4 DarkTeal = new(0.0f, 0.5f, 0.25f);
        public static readonly ColorF4 LightOlive = new(0.75f, 1.0f, 0.5f);
        public static readonly ColorF4 DarkOlive = new(0.25f, 0.5f, 0.0f);
        public static readonly ColorF4 LightPeach = new(1.0f, 0.75f, 0.5f);
        public static readonly ColorF4 DarkPeach = new(0.5f, 0.25f, 0.0f);
        public static readonly ColorF4 LightLavender = new(0.75f, 0.5f, 1.0f);
        public static readonly ColorF4 DarkLavender = new(0.25f, 0.0f, 0.5f);
        public static readonly ColorF4 LightMaroon = new(0.75f, 0.25f, 0.0f);
        public static readonly ColorF4 DarkMaroon = new(0.25f, 0.0f, 0.0f);
        public static readonly ColorF4 LightGold = new(1.0f, 0.75f, 0.0f);
        public static readonly ColorF4 DarkGold = new(0.5f, 0.25f, 0.0f);
        public static readonly ColorF4 LightSilver = new(0.75f, 0.75f, 0.75f);
        public static readonly ColorF4 DarkSilver = new(0.25f, 0.25f, 0.25f);
        public static readonly ColorF4 Charcoal = new(0.125f, 1.0f);

        public float R, G, B, A;

        [YamlIgnore]
        [MemoryPackIgnore]
        [Browsable(false)]
        public string HexCode
        {
            readonly get => 
                R.ToByte().ToString("X2") +
                G.ToByte().ToString("X2") + 
                B.ToByte().ToString("X2") + 
                A.ToByte().ToString("X2");
            set
            {
                R = 0.0f;
                G = 0.0f;
                B = 0.0f;

                if (value.StartsWith('#'))
                    value = value[1..];
                if (value.StartsWith("0x"))
                    value = value[2..];

                if (value.Length >= 2)
                {
                    string r = value[..2];
                    byte.TryParse(r, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte rb);
                    R = rb / 255.0f;
                    if (value.Length >= 4)
                    {
                        string g = value.Substring(2, 2);
                        byte.TryParse(g, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte gb);
                        G = gb / 255.0f;
                        if (value.Length >= 6)
                        {
                            string b = value.Substring(4, 2);
                            byte.TryParse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte bb);
                            B = bb / 255.0f;
                            if (value.Length >= 8)
                            {
                                string a = value.Substring(6, 2);
                                byte.TryParse(a, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte ab);
                                A = ab / 255.0f;
                            }
                        }

                    }
                }
            }
        }

        public ColorF4(float intensity) { R = G = B = intensity; A = 1.0f; }
        public ColorF4(float intensity, float alpha) { R = G = B = intensity; A = alpha; }
        public ColorF4(float r, float g, float b) { R = r; G = g; B = b; A = 1.0f; }
        public ColorF4(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
        public ColorF4(string s)
        {
            R = G = B = 0.0f;
            A = 1.0f;

            char[] delims = [',', 'R', 'G', 'B', 'A', ':', ' ', '[', ']'];
            string[] arr = s.Split(delims, StringSplitOptions.RemoveEmptyEntries);

            if (arr.Length >= 4)
            {
                float.TryParse(arr[0], NumberStyles.Any, CultureInfo.InvariantCulture, out R);
                float.TryParse(arr[1], NumberStyles.Any, CultureInfo.InvariantCulture, out G);
                float.TryParse(arr[2], NumberStyles.Any, CultureInfo.InvariantCulture, out B);
                float.TryParse(arr[3], NumberStyles.Any, CultureInfo.InvariantCulture, out A);
            }
        }

        [Browsable(false)]
        public float* Data => (float*)Address;

        [Browsable(false)]
        public VoidPtr Address { get { fixed (void* p = &this) return p; } }

        [YamlIgnore]
        [MemoryPackIgnore]
        [Browsable(false)]
        public Color Color
        {
            readonly get => (Color)this;
            set => this = value;
        }

        public readonly void Write(VoidPtr address)
            => *(ColorF4*)address = this;

        public void Read(VoidPtr address)
            => this = *(ColorF4*)address;

        private const float ByteToFloat = 1.0f / 255.0f;

        public static implicit operator ColorF4(RGBAPixel p)
            => new(p.R * ByteToFloat, p.G * ByteToFloat, p.B * ByteToFloat, p.A * ByteToFloat);
        public static implicit operator ColorF4(ARGBPixel p)
            => new(p.R * ByteToFloat, p.G * ByteToFloat, p.B * ByteToFloat, p.A * ByteToFloat);
        public static implicit operator ColorF4(Color p)
            => new(p.R * ByteToFloat, p.G * ByteToFloat, p.B * ByteToFloat, p.A * ByteToFloat);
        public static explicit operator Color(ColorF4 p)
            => Color.FromArgb(p.A.ToByte(), p.R.ToByte(), p.G.ToByte(), p.B.ToByte());
        public static implicit operator ColorF4(Vector3 v)
            => new(v.X, v.Y, v.Z, 1.0f);
        public static implicit operator ColorF4(Vector4 v)
            => new(v.X, v.Y, v.Z, v.W);
        public static implicit operator Vector4(ColorF4 v)
            => new(v.R, v.G, v.B, v.A);
        public static implicit operator ColorF4(ColorF3 p)
            => new(p.R, p.G, p.B, 1.0f);

        public static ColorF4 operator -(ColorF4 left, ColorF4 right)
            => new(
                left.R - right.R,
                left.G - right.G,
                left.B - right.B,
                left.A - right.A);

        public static ColorF4 operator +(ColorF4 left, ColorF4 right)
            => new(
                left.R + right.R,
                left.G + right.G,
                left.B + right.B,
                left.A + right.A);

        public static ColorF4 operator *(ColorF4 left, float right)
            => new(
                left.R * right,
                left.G * right,
                left.B * right,
                left.A * right);

        public static ColorF4 operator *(float left, ColorF4 right)
            => new(
                left * right.R,
                left * right.G,
                left * right.B,
                left * right.A);

        public readonly bool Equals(ColorF4 other, float precision)
        {
            return
                Math.Abs(R - other.R) < precision &&
                Math.Abs(G - other.G) < precision &&
                Math.Abs(B - other.B) < precision &&
                Math.Abs(A - other.A) < precision;
        }

        public override readonly string ToString()
            => string.Format("[R:{0},G:{1},B:{2},A:{3}]", R, G, B, A);
    }
}
