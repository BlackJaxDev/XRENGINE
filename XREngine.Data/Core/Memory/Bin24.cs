using Extensions;
using System.Runtime.InteropServices;
using XREngine.Data;

namespace XREngine.Core.Memory
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Bin24(UInt24 val)
    {
        public UInt24 _data = val;

        public static implicit operator int(Bin24 val) { return (int)val._data; }
        public static implicit operator uint(Bin24 val) { return (uint)val._data; }
        public static implicit operator Bin24(uint val) { return new Bin24((UInt24)val); }
        public static implicit operator UInt24(Bin24 val) { return val._data; }
        public static implicit operator Bin24(UInt24 val) { return new Bin24(val); }

        public override readonly string ToString()
        {
            int i = 0;
            string val = "";
            uint data = (uint)_data;
            while (i++ < 24)
            {
                val += (data >> (24 - i)) & 1;
                if (i % 4 == 0 && i != 24)
                    val += " ";
            }
            return val;
        }

        public bool this[int index]
        {
            readonly get => ((uint)_data >> index & 1) != 0;
            set
            {
                if (value)
                    _data = (UInt24)((uint)_data | (uint)(1 << index));
                else
                    _data = (UInt24)((uint)_data & ~(uint)(1 << index));
            }
        }

        //public uint this[int shift, int mask]
        //{
        //    get { return (uint)(data >> shift & mask); }
        //    set { data = (uint)((data & ~(mask << shift)) | ((value & mask) << shift)); }
        //}

        public int this[int shift, int bitCount]
        {
            readonly get
            {
                int mask = 0;
                for (int i = 0; i < bitCount; i++)
                    mask |= 1 << i;
                return (int)(((uint)_data >> shift) & mask);
            }
            set
            {
                int mask = 0;
                for (int i = 0; i < bitCount; i++)
                    mask |= 1 << i;
                _data = (UInt24)(uint)(((uint)_data & ~(mask << shift)) | (((uint)value & mask) << shift));
            }
        }

        public static Bin24 FromString(string s)
        {
            char[] delims = [',', '(', ')', ' '];

            uint b = 0;
            string[] arr = s.Split(delims, StringSplitOptions.RemoveEmptyEntries);

            for (int len = 0; len < arr.Length; len++)
            {
                byte bit = 0;
                for (int i = 0; i < arr[len].Length; i++)
                {
                    b <<= 1;
                    byte.TryParse(arr[len][i].ToString(), out bit);
                    bit = bit.Clamp(0, 1);
                    b += bit;
                }
            }

            return new Bin24((UInt24)b);
        }
    }
}
