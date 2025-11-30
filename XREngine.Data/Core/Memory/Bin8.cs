using Extensions;
using System.Runtime.InteropServices;

namespace XREngine.Core.Memory
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Bin8(byte val)
    {
        public byte _data = val;

        public static implicit operator byte(Bin8 val) { return val._data; }
        public static implicit operator Bin8(byte val) { return new Bin8(val); }
        //public static implicit operator sbyte(Bin8 val) { return (sbyte)val._data; }
        //public static implicit operator Bin8(sbyte val) { return new Bin8((byte)val); }

        public override readonly string ToString()
        {
            int i = 0;
            string val = "";
            while (i++ < 8)
            {
                val += (_data >> (8 - i)) & 1;
                if (i % 4 == 0 && i != 8)
                    val += " ";
            }
            return val;
        }

        public bool this[int index]
        {
            readonly get => (_data >> index & 1) != 0;
            set
            {
                if (value)
                    _data |= (byte)(1 << index);
                else
                    _data &= (byte)~(1 << index);
            }
        }

        //public byte this[int shift, int mask]
        //{
        //    get { return (byte)(data >> shift & mask); }
        //    set { data = (byte)((data & ~(mask << shift)) | ((value & mask) << shift)); }
        //}

        public byte this[int shift, int bitCount]
        {
            readonly get
            {
                int mask = 0;
                for (int i = 0; i < bitCount; i++)
                    mask |= 1 << i;
                return (byte)((_data >> shift) & mask);
            }
            set
            {
                int mask = 0;
                for (int i = 0; i < bitCount; i++)
                    mask |= 1 << i;
                _data = (byte)((_data & ~(mask << shift)) | ((value & mask) << shift));
            }
        }

        public static Bin8 FromString(string s)
        {
            char[] delims = [',', '(', ')', ' '];

            byte b = 0;
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

            return new Bin8(b);
        }
    }
}
