using System.Runtime.InteropServices;
using XREngine.Data;

namespace XREngine.Core.Memory
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FloatQuantizeHeader
    {
        public const int Size = 0x10;

        private Bin32 _flags;
        private bfloat _divisor;
        private bint _elementCount;
        private bint _dataLength;
        
        public int ElementCount
        {
            readonly get => _elementCount;
            set => _elementCount = value;
        }
        public int DataLength
        {
            readonly get => _dataLength;
            set => _dataLength = value;
        }
        public float Divisor
        {
            readonly get => _divisor;
            set => _divisor = value;
        }
        public int BitCount
        {
            get => (int)_flags[7, 5];
            set
            {
                if (value < 1 || value > 32)
                    throw new InvalidOperationException("Bit count must be between 1 and 32.");
                _flags[7, 5] = (uint)value;
            }
        }
        public bool HasW { get { return _flags[6]; } set { _flags[6] = value; } }
        public bool HasZ { get { return _flags[5]; } set { _flags[5] = value; } }
        public bool HasY { get { return _flags[4]; } set { _flags[4] = value; } }
        public bool HasX { get { return _flags[3]; } set { _flags[3] = value; } }
        public bool Signed { get { return _flags[2]; } set { _flags[2] = value; } }
        public int ComponentCount
        {
            get { return (int)(_flags[0, 2] + 1); }
            set
            {
                if (value < 1 || value > 4)
                    throw new InvalidOperationException("Component count must be 1, 2, 3 or 4.");
                _flags[0, 2] = (byte)(value - 1);
            }
        }
    }
}
