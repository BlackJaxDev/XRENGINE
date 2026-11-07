using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// Reads the little-endian values of a cooked <see cref="SubMesh"/> payload from a span. A read past the end throws
    /// <see cref="InvalidDataException"/>. The string form is the one of <see cref="BinaryWriter.Write(string)"/>.
    /// </summary>
    internal ref struct SubMeshCookedPayloadReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        /// <summary>The number of bytes that are not read yet.</summary>
        public readonly int Remaining => _data.Length - _position;

        public ReadOnlySpan<byte> ReadBytes(int length)
        {
            if (length < 0 || length > Remaining)
                throw new InvalidDataException($"The cooked sub-mesh payload ends {length - Remaining} bytes early.");
            ReadOnlySpan<byte> bytes = _data.Slice(_position, length);
            _position += length;
            return bytes;
        }

        public byte ReadByte()
            => ReadBytes(1)[0];

        public bool ReadBoolean()
            => ReadByte() != 0;

        public ushort ReadUInt16()
            => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(sizeof(ushort)));

        public int ReadInt32()
            => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(sizeof(int)));

        public uint ReadUInt32()
            => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint)));

        public ulong ReadUInt64()
            => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(sizeof(ulong)));

        public float ReadSingle()
            => BinaryPrimitives.ReadSingleLittleEndian(ReadBytes(sizeof(float)));

        public Vector3 ReadVector3()
            => new(ReadSingle(), ReadSingle(), ReadSingle());

        public Guid ReadGuid()
            => new(ReadBytes(16));

        public UInt128 ReadUInt128()
            => BinaryPrimitives.ReadUInt128BigEndian(ReadBytes(16));

        public string ReadString()
        {
            int length = Read7BitEncodedInt();
            return length == 0 ? string.Empty : Encoding.UTF8.GetString(ReadBytes(length));
        }

        public string? ReadNullableString()
            => ReadBoolean() ? ReadString() : null;

        private int Read7BitEncodedInt()
        {
            uint result = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte value = ReadByte();
                result |= (uint)(value & 0x7F) << shift;
                if ((value & 0x80) == 0)
                    return result <= int.MaxValue ? (int)result : throw new InvalidDataException("A string length in the cooked sub-mesh payload is too large.");
            }
            throw new InvalidDataException("A string length in the cooked sub-mesh payload is not valid.");
        }
    }
}
