using Extensions;
using System.Runtime.InteropServices;

namespace XREngine.Core.Memory
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Bin32(uint val)
    {
        public uint _data = val;

        public static implicit operator uint(Bin32 val) { return val._data; }
        public static implicit operator Bin32(uint val) { return new Bin32(val); }
        //public static implicit operator int(Bin32 val) { return (int)val._data; }
        //public static implicit operator Bin32(int val) { return new Bin32((uint)val); }

        public override readonly string ToString()
        {
            int i = 0;
            string val = "";
            while (i++ < 32)
            {
                val += (_data >> (32 - i)) & 1;
                if (i % 4 == 0 && i != 32)
                    val += " ";
            }
            return val;
        }

        public bool this[int index]
        {
            readonly get => ((_data >> index) & 1) != 0;
            set
            {
                if (value)
                    _data |= (uint)(1 << index);
                else
                    _data &= ~(uint)(1 << index);
            }
        }

        //public uint this[int shift, int mask]
        //{
        //    get { return (uint)(data >> shift & mask); }
        //    set { data = (uint)((data & ~(mask << shift)) | ((value & mask) << shift)); }
        //}

        public uint this[int shift, int bitCount]
        {
            readonly get
            {
                int mask = 0;
                for (int i = 0; i < bitCount; i++)
                    mask |= 1 << i;
                return (uint)((_data >> shift) & mask);
            }
            set
            {
                int mask = 0;
                for (int i = 0; i < bitCount; i++)
                    mask |= 1 << i;
                _data = (uint)((_data & ~(mask << shift)) | ((value & mask) << shift));
            }
        }

        public static Bin32 FromString(string s)
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

            return new Bin32(b);
        }
    }
}