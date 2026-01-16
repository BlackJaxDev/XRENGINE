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
    public unsafe struct ColorF3
    {
        public float R, G, B;

        [YamlIgnore]
        [MemoryPackIgnore]
        [Browsable(false)]
        public string HexCode
        {
            readonly get => 
                R.ToByte().ToString("X2") + 
                G.ToByte().ToString("X2") + 
                B.ToByte().ToString("X2");
            set
            {
                R = 0.0f;
                G = 0.0f;
                B = 0.0f;

                if (value.StartsWith('#'))
                    value = value[1..];
                if (value.StartsWith("0x"))
                    value = value[2..];

                if (value.Length < 2)
                    return;

                string r = value[..2];
                byte.TryParse(r, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte rb);
                R = rb / 255.0f;

                if (value.Length < 4)
                    return;
                
                string g = value.Substring(2, 2);
                byte.TryParse(g, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte gb);
                G = gb / 255.0f;
                if (value.Length >= 6)
                {
                    string b = value.Substring(4, 2);
                    byte.TryParse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte bb);
                    B = bb / 255.0f;
                }
            }
        }

        public ColorF3(float rgb) { R = G = B = rgb; }
        public ColorF3(float r, float g, float b) { R = r; G = g; B = b; }
        public ColorF3(string s)
        {
            R = G = B = 0.0f;

            char[] delims = [',', 'R', 'G', 'B', ':', ' ', '[', ']'];
            string[] arr = s.Split(delims, StringSplitOptions.RemoveEmptyEntries);

            if (arr.Length >= 3)
            {
                float.TryParse(arr[0], NumberStyles.Any, CultureInfo.InvariantCulture, out R);
                float.TryParse(arr[1], NumberStyles.Any, CultureInfo.InvariantCulture, out G);
                float.TryParse(arr[2], NumberStyles.Any, CultureInfo.InvariantCulture, out B);
            }
        }

        [Browsable(false)]
        public float* Data => (float*)Address;

        [Browsable(false)]
        public VoidPtr Address { get { fixed (void* p = &this) return p; } }

        public readonly void Write(VoidPtr address)
            => *(ColorF3*)address = this;

        public void Read(VoidPtr address)
            => this = *(ColorF3*)address;

        private const float ByteToFloat = 1.0f / 255.0f;

        public static readonly ColorF3 Red = new(1.0f, 0.0f, 0.0f);
        public static readonly ColorF3 Green = new(0.0f, 1.0f, 0.0f);
        public static readonly ColorF3 Blue = new(0.0f, 0.0f, 1.0f);
        public static readonly ColorF3 White = new(1.0f, 1.0f, 1.0f);
        public static readonly ColorF3 Black = new(0.0f, 0.0f, 0.0f);
        public static readonly ColorF3 Yellow = new(1.0f, 1.0f, 0.0f);
        public static readonly ColorF3 Cyan = new(0.0f, 1.0f, 1.0f);
        public static readonly ColorF3 Magenta = new(1.0f, 0.0f, 1.0f);

        public static implicit operator ColorF3(RGBAPixel p) => new(p.R * ByteToFloat, p.G * ByteToFloat, p.B * ByteToFloat);
        public static implicit operator ColorF3(ARGBPixel p) => new(p.R * ByteToFloat, p.G * ByteToFloat, p.B * ByteToFloat);
        public static explicit operator ColorF3(Vector4 v) => new(v.X, v.Y, v.Z);
        public static explicit operator ColorF3(ColorF4 p) => new(p.R, p.G, p.B);

        public static implicit operator ColorF3(Vector3 v) => new(v.X, v.Y, v.Z);
        public static implicit operator Vector3(ColorF3 v) => new(v.R, v.G, v.B);
        public static explicit operator ColorF3(Color p) => new(p.R * ByteToFloat, p.G * ByteToFloat, p.B * ByteToFloat);
        public static explicit operator Color(ColorF3 p) => Color.FromArgb(255, p.R.ToByte(), p.G.ToByte(), p.B.ToByte());

        public static ColorF3 operator -(ColorF3 left, ColorF3 right)
            => new(
                left.R - right.R,
                left.G - right.G,
                left.B - right.B);

        public static ColorF3 operator +(ColorF3 left, ColorF3 right)
            => new(
                left.R + right.R,
                left.G + right.G,
                left.B + right.B);

        public static ColorF3 operator *(ColorF3 left, float right)
            => new(
                left.R * right,
                left.G * right,
                left.B * right);

        public static ColorF3 operator *(float left, ColorF3 right)
            => new(
                left * right.R,
                left * right.G,
                left * right.B);

        public override readonly string ToString()
            => string.Format("[R:{0},G:{1},B:{2}]", R, G, B);

        public readonly string WriteToString()
            => ToString();

        public void ReadFromString(string str)
            => this = new ColorF3(str);

        [YamlIgnore]
        [MemoryPackIgnore]
        [Browsable(false)]
        public Color Color 
        {
            readonly get => (Color)this;
            set => this = (ColorF3)value;
        }
    }
}
