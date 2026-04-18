using XREngine.Extensions;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace XREngine.Data.Colors
{
    [Serializable]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct RGBPixel(byte r, byte g, byte b)
    {
        public byte R = r, G = g, B = b;

        public byte* Data { get { return (byte*)Address; } }
        public VoidPtr Address { get { fixed (void* p = &this) return p; } }

        public void Write(VoidPtr address)
            => this = *(RGBPixel*)address;
        public readonly void Read(VoidPtr address)
            => *(RGBPixel*)address = this;

        public static explicit operator RGBPixel(ColorF3 p) => new(p.R.ToByte(), p.G.ToByte(), p.B.ToByte());
        public static explicit operator RGBPixel(ColorF4 p) => new(p.R.ToByte(), p.G.ToByte(), p.B.ToByte());
        public static explicit operator RGBPixel(ARGBPixel p) => new(p.R, p.G, p.B);
        public static explicit operator RGBPixel(Color p) => new(p.R, p.G, p.B);
        public static implicit operator Color(RGBPixel p) => Color.FromArgb(255, p.R, p.G, p.B);

        [Browsable(false)]
        public Color Color
        {
            readonly get => this;
            set => this = (RGBPixel)value;
        }
    }
}
