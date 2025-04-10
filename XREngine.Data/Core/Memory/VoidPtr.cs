using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace XREngine.Data
{
    [Serializable]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct VoidPtr
    {
        private void* _address;

        public static readonly VoidPtr Zero = new() { _address = null };

        public readonly void* Pointer => _address;

        #region Primitive Types
        public readonly byte Byte
        {
            get => *(byte*)_address;
            set => *(byte*)_address = value;
        }
        public readonly byte GetByte() => Byte;
        public void SetByte(byte i) => Byte = i;
        public readonly sbyte SByte
        {
            get => *(sbyte*)_address;
            set => *(sbyte*)_address = value;
        }
        public readonly sbyte GetSByte() => SByte;
        public void SetSByte(sbyte i) => SByte = i;
        public readonly ushort UShort
        {
            get => *(bushort*)_address;
            set => *(bushort*)_address = value;
        }
        public readonly ushort GetUShort() => UShort;
        public void SetUShort(ushort i) => UShort = i;
        public readonly short Short
        {
            get => *(bshort*)_address;
            set => *(bshort*)_address = value;
        }
        public readonly short GetShort() => Short;
        public void SetShort(short i) => Short = i;
        public readonly uint UInt
        {
            get => *(buint*)_address;
            set => *(buint*)_address = value;
        }
        public readonly uint GetUInt() => UInt;
        public void SetUInt(uint i) => UInt = i;
        public readonly int Int
        {
            get => *(bint*)_address;
            set => *(bint*)_address = value;
        }
        public readonly int GetInt() => Int;
        public void SetInt(int i) => Int = i;
        public readonly ulong ULong
        {
            get => *(bulong*)_address;
            set => *(bulong*)_address = value;
        }
        public readonly ulong GetULong() => ULong;
        public void SetULong(ulong i) => ULong = i;
        public readonly long Long
        {
            get => *(blong*)_address;
            set => *(blong*)_address = value;
        }
        public readonly long GetLong() => Long;
        public void SetLong(long i) => Long = i;
        public readonly float Float
        {
            get => *(bfloat*)_address;
            set => *(bfloat*)_address = value;
        }
        public readonly float GetFloat() => Float;
        public void SetFloat(float i) => Float = i;
        public readonly Vector2 Vector2
        {
            get => *(Vector2*)_address;
            set => *(Vector2*)_address = value;
        }
        public readonly Vector2 GetVector2() => Vector2;
        public void SetVector2(Vector2 i) => Vector2 = i;
        public readonly Vector3 Vector3
        {
            get => *(Vector3*)_address;
            set => *(Vector3*)_address = value;
        }
        public readonly Vector3 GetVector3() => Vector3;
        public void SetVector3(Vector3 i) => Vector3 = i;
        public readonly Vector4 Vector4
        {
            get => *(Vector4*)_address;
            set => *(Vector4*)_address = value;
        }
        public readonly Vector4 GetVector4() => Vector4;
        public void SetVector4(Vector4 i) => Vector4 = i;
        public readonly double Double
        {
            get => *(bdouble*)_address;
            set => *(bdouble*)_address = value;
        }
        public readonly double GetDouble() => Double;
        public void GetDouble(double i) => Double = i;
        public readonly Int24 Int24
        {
            get => *(BInt24*)_address;
            set => *(BInt24*)_address = value;
        }
        public readonly Int24 GetInt24() => Int24;
        public void SetInt24(Int24 i) => Int24 = i;
        public readonly UInt24 UInt24
        {
            get => *(BUInt24*)_address;
            set => *(BUInt24*)_address = value;
        }
        public readonly BUInt24 GetUInt24() => UInt24;
        public void SetUInt24(BUInt24 i) => UInt24 = i;
        public readonly char Char
        {
            get => *(char*)_address;
            set => *(char*)_address = value;
        }
        public readonly char GetChar() => Char;
        public void SetChar(char i) => Char = i;
        public readonly decimal Decimal
        {
            get => *(decimal*)_address;
            set => *(decimal*)_address = value;
        }
        public readonly decimal GetDecimal() => Decimal;
        public void SetDecimal(decimal i) => Decimal = i;
        #endregion

        #region Operators
        public static int operator -(VoidPtr p1, VoidPtr p2)
            => checked((int)((byte*)p1._address - (byte*)p2._address));

        public static VoidPtr operator +(VoidPtr p1, uint addr)
            => new() { _address = ((byte*)p1._address + addr) };
        public static VoidPtr operator -(VoidPtr p1, uint addr)
            => new() { _address = ((byte*)p1._address - addr) };

        public static VoidPtr operator +(VoidPtr p1, int addr)
            => new() { _address = ((byte*)p1._address + addr) };
        public static VoidPtr operator -(VoidPtr p1, int addr)
            => new() { _address = ((byte*)p1._address - addr) };

        public static VoidPtr operator +(VoidPtr p1, ulong addr)
            => new() { _address = ((byte*)p1._address + addr) };
        public static VoidPtr operator -(VoidPtr p1, ulong addr)
            => new() { _address = ((byte*)p1._address - addr) };

        public static VoidPtr operator +(VoidPtr p1, long addr)
            => new() { _address = ((byte*)p1._address + addr) };
        public static VoidPtr operator -(VoidPtr p1, long addr)
            => new() { _address = ((byte*)p1._address - addr) };

        public static bool operator >(VoidPtr p1, VoidPtr p2)
            => p1._address > p2._address;
        public static bool operator <(VoidPtr p1, VoidPtr p2)
            => p1._address < p2._address;
        public static bool operator >=(VoidPtr p1, VoidPtr p2)
            => p1._address >= p2._address;
        public static bool operator <=(VoidPtr p1, VoidPtr p2)
            => p1._address <= p2._address;
        public static bool operator ==(VoidPtr p1, VoidPtr p2)
            => p1._address == p2._address;
        public static bool operator !=(VoidPtr p1, VoidPtr p2)
            => p1._address != p2._address;
        #endregion

        #region Casts
        public static implicit operator bool(VoidPtr ptr)
            => ptr._address != null;

        public static implicit operator void*(VoidPtr ptr)
            => ptr._address;
        public static implicit operator VoidPtr(void* ptr)
            => new() { _address = ptr };

        public static implicit operator uint(VoidPtr ptr)
            => checked((uint)ptr._address);
        public static implicit operator VoidPtr(uint ptr)
            => new() { _address = (void*)ptr };
        public static implicit operator int(VoidPtr ptr)
            => checked((int)ptr._address);
        public static implicit operator VoidPtr(int ptr)
            => new() { _address = (void*)ptr };

        public static implicit operator ulong(VoidPtr ptr)
            => (ulong)ptr._address;
        public static implicit operator VoidPtr(ulong ptr)
            => new() { _address = (void*)ptr };
        public static implicit operator long(VoidPtr ptr)
            => (long)ptr._address;
        public static implicit operator VoidPtr(long ptr)
            => new() { _address = (void*)ptr };

        public static implicit operator VoidPtr(IntPtr ptr)
            => new() { _address = (void*)ptr };
        public static implicit operator IntPtr(VoidPtr ptr)
            => (IntPtr)ptr._address;

        //public static implicit operator sbyte*(VoidPtr ptr) { return (sbyte*)ptr.address; }
        //public static implicit operator VoidPtr(sbyte* ptr) { return new VoidPtr() { address = ptr }; }

        //public static implicit operator byte* (VoidPtr ptr) { return (byte*)ptr.address; }
        //public static implicit operator VoidPtr(byte* ptr) { return new VoidPtr() { address = ptr }; }
        #endregion

        #region Incremental Writing Methods
        public void WriteByte(byte value, bool incrementPointer = true)
        {
            Byte = value;
            if (incrementPointer)
                _address = (byte*)_address + 1;
        }
        public void WriteSByte(sbyte value, bool incrementPointer = true)
        {
            SByte = value;
            if (incrementPointer)
                _address = (sbyte*)_address + 1;
        }
        public void WriteShort(short value, bool incrementPointer = true)
        {
            Short = value;
            if (incrementPointer)
                _address = (bshort*)_address + 1;
        }
        public void WriteUShort(ushort value, bool incrementPointer = true)
        {
            UShort = value;
            if (incrementPointer)
                _address = (bushort*)_address + 1;
        }
        public void WriteInt(int value, bool incrementPointer = true)
        {
            Int = value;
            if (incrementPointer)
                _address = (bint*)_address + 1;
        }
        public void WriteUInt(uint value, bool incrementPointer = true)
        {
            UInt = value;
            if (incrementPointer)
                _address = (buint*)_address + 1;
        }
        public void WriteLong(long value, bool incrementPointer = true)
        {
            Long = value;
            if (incrementPointer)
                _address = (blong*)_address + 1;
        }
        public void WriteULong(ulong value, bool incrementPointer = true)
        {
            ULong = value;
            if (incrementPointer)
                _address = (bulong*)_address + 1;
        }
        public void WriteFloat(float value, bool incrementPointer = true)
        {
            Float = value;
            if (incrementPointer)
                _address = (bfloat*)_address + 1;
        }
        public void WriteDouble(double value, bool incrementPointer = true)
        {
            Double = value;
            if (incrementPointer)
                _address = (bdouble*)_address + 1;
        }
        public void WriteDecimal(decimal value, bool incrementPointer = true)
        {
            Decimal = value;
            if (incrementPointer)
                _address = (decimal*)_address + 1;
        }
        public void WriteUInt24(uint value, bool incrementPointer = true)
        {
            UInt24 = value;
            if (incrementPointer)
                _address = (BUInt24*)_address + 1;
        }
        public void WriteInt24(int value, bool incrementPointer = true)
        {
            Int24 = value;
            if (incrementPointer)
                _address = (BInt24*)_address + 1;
        }
        public void WriteChar(char value, bool incrementPointer = true)
        {
            Char = value;
            if (incrementPointer)
                _address = (char*)_address + 1;
        }
        #endregion

        #region Incremental Reading Methods
        public byte ReadByte(bool incrementPointer = true)
        {
            byte value = Byte;
            if (incrementPointer)
                _address = (byte*)_address + 1;
            return value;
        }
        public sbyte ReadSByte(bool incrementPointer = true)
        {
            sbyte value = SByte;
            if (incrementPointer)
                _address = (sbyte*)_address + 1;
            return value;
        }
        public short ReadShort(bool incrementPointer = true)
        {
            short value = Short;
            if (incrementPointer)
                _address = (bshort*)_address + 1;
            return value;
        }
        public ushort ReadUShort(bool incrementPointer = true)
        {
            ushort value = UShort;
            if (incrementPointer)
                _address = (bushort*)_address + 1;
            return value;
        }
        public int ReadInt(bool incrementPointer = true)
        {
            int value = Int;
            if (incrementPointer)
                _address = (bint*)_address + 1;
            return value;
        }
        public uint ReadUInt(bool incrementPointer = true)
        {
            uint value = UInt;
            if (incrementPointer)
                _address = (buint*)_address + 1;
            return value;
        }
        public long ReadLong(bool incrementPointer = true)
        {
            long value = Long;
            if (incrementPointer)
                _address = (blong*)_address + 1;
            return value;
        }
        public ulong ReadULong(bool incrementPointer = true)
        {
            ulong value = ULong;
            if (incrementPointer)
                _address = (bulong*)_address + 1;
            return value;
        }
        public float ReadFloat(bool incrementPointer = true)
        {
            float value = Float;
            if (incrementPointer)
                _address = (bfloat*)_address + 1;
            return value;
        }
        public double ReadDouble(bool incrementPointer = true)
        {
            double value = Double;
            if (incrementPointer)
                _address = (bdouble*)_address + 1;
            return value;
        }
        public decimal ReadDecimal(bool incrementPointer = true)
        {
            decimal value = Decimal;
            if (incrementPointer)
                _address = (decimal*)_address + 1;
            return value;
        }
        public uint ReadUInt24(bool incrementPointer = true)
        {
            uint value = UInt24;
            if (incrementPointer)
                _address = (BUInt24*)_address + 1;
            return value;
        }
        public int ReadInt24(bool incrementPointer = true)
        {
            int value = Int24;
            if (incrementPointer)
                _address = (BInt24*)_address + 1;
            return value;
        }
        public char ReadChar(bool incrementPointer = true)
        {
            char value = Char;
            if (incrementPointer)
                _address = (char*)_address + 1;
            return value;
        }
        #endregion

        public readonly byte[] GetBytes(int count)
        {
            byte[] arr = new byte[count];
            for (int i = 0; i < count; ++i)
                arr[i] = this[i].Byte;
            return arr;
        }

        public readonly VoidPtr this[int index] => this + index;
        public readonly VoidPtr this[int count, int stride] => this + (count * stride);
        public readonly VoidPtr this[uint index] => this + index;
        public readonly VoidPtr this[uint count, uint stride] => this + (count * stride);

        public readonly string GetANSIString(int offset = 0) => new((sbyte*)this + offset);
        public readonly string GetUnicodeString(int offset = 0) => new((char*)this + offset);
        public readonly string GetString(int offset, int length, Encoding enc) => enc.GetString((byte*)this + offset, length);

        public void Offset(int offset) => _address = (byte*)_address + offset;

        public static void Swap(float* p1, float* p2) => (*p2, *p1) = (*p1, *p2);
        public static void Swap(int* p1, int* p2) => (*p2, *p1) = (*p1, *p2);
        public static void Swap(short* p1, short* p2) => (*p2, *p1) = (*p1, *p2);
        public static void Swap(ushort* p1, ushort* p2) => (*p2, *p1) = (*p1, *p2);
        public static void Swap(byte* p1, byte* p2) => (*p2, *p1) = (*p1, *p2);

        public override readonly int GetHashCode() => (int)_address;
        public override readonly bool Equals(object? obj) => base.Equals(obj);

        public readonly T* As<T>() where T : unmanaged => (T*)_address;
    }
}
