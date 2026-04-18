using XREngine.Extensions;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace XREngine.Data.Colors
{
    [Serializable]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct RGBAPixel(byte r, byte g, byte b, byte a)
    {
        public byte R = r, G = g, B = b, A = a;

        public byte* Data => (byte*)Address;
        public VoidPtr Address { get { fixed (void* p = &this) return p; } }

        public void Write(VoidPtr address)
            => this = *(RGBAPixel*)address;
        public readonly void Read(VoidPtr address)
            => *(RGBAPixel*)address = this;

        public static explicit operator RGBAPixel(ColorF4 p) => new(p.R.ToByte(), p.G.ToByte(), p.B.ToByte(), p.A.ToByte());
        public static implicit operator RGBAPixel(ARGBPixel p) => new(p.R, p.G, p.B, p.A);
        public static implicit operator RGBAPixel(Color p) => new(p.R, p.G, p.B, p.A);
        public static implicit operator Color(RGBAPixel p) => Color.FromArgb(p.A, p.R, p.G, p.B);

        [Browsable(false)]
        public Color Color 
        {
            readonly get => this;
            set => this = value;
        }
    }
}
