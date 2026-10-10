using System.Numerics;

namespace XREngine.Data
{
    public static partial class Compression
    {
        /// <summary>
        /// Compresses a unit quaternion q = [x, y, z, w] into a compressed form.
        /// N is the number of bits per component for the quantized components.
        /// </summary>
        public static (int index, int signBit, int[] quantizedComponents) CompressQuaternion(Quaternion q, int bitsPerComponent = 8)
        {
            ValidateQuaternionBits(bitsPerComponent);
            float lengthSquared = q.LengthSquared();
            if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-12f)
                throw new ArgumentException("Quaternion compression requires a finite, nonzero rotation.", nameof(q));
            q = Quaternion.Normalize(q);
            int index = 0;
            for (int component = 1; component < 4; component++)
                if (MathF.Abs(q[component]) > MathF.Abs(q[index]))
                    index = component;

            int signBit = q[index] >= 0 ? 0 : 1;
            int[] quantizedComponents = new int[3];
            int maxInt = (1 << bitsPerComponent) - 1;
            int retained = 0;
            for (int component = 0; component < 4; component++)
            {
                if (component == index)
                    continue;
                // Preserve the retained components' magnitude. Normalizing this triple loses the rotation angle.
                int value = (int)MathF.Round((q[component] + 1.0f) * (maxInt / 2.0f));
                quantizedComponents[retained++] = Math.Clamp(value, 0, maxInt);
            }
            return (index, signBit, quantizedComponents);
        }

        public static Quaternion DecompressQuaternion((int index, int signBit, int[] quantizedComponents) compressedData, int bitsPerComponent = 8)
        {
            ValidateQuaternionBits(bitsPerComponent);
            if (compressedData.index is < 0 or > 3 || compressedData.signBit is < 0 or > 1
                || compressedData.quantizedComponents is not { Length: 3 })
                throw new ArgumentException("Invalid compressed quaternion fields.", nameof(compressedData));
            int maxInt = (1 << bitsPerComponent) - 1;
            Quaternion result = default;
            float sumOfSquares = 0;
            int retained = 0;
            for (int component = 0; component < 4; component++)
            {
                if (component == compressedData.index)
                    continue;
                int quantized = compressedData.quantizedComponents[retained++];
                if (quantized < 0 || quantized > maxInt)
                    throw new ArgumentException("Compressed quaternion component exceeds its bit range.", nameof(compressedData));
                float value = quantized / (maxInt / 2.0f) - 1.0f;
                result[component] = value;
                sumOfSquares += value * value;
            }
            float omitted = MathF.Sqrt(MathF.Max(0, 1 - sumOfSquares));
            result[compressedData.index] = compressedData.signBit == 0 ? omitted : -omitted;
            return Quaternion.Normalize(result);
        }

        private static void ValidateQuaternionBits(int bitsPerComponent)
        {
            if (bitsPerComponent is < 2 or > 20)
                throw new ArgumentOutOfRangeException(nameof(bitsPerComponent), "Quaternion components require 2 to 20 bits to fit the packed wire representation.");
        }

        /// <summary>
        /// Compresses a unit quaternion q = [x, y, z, w] into a byte array.
        /// N is the number of bits per component for the quantized components.
        /// </summary>
        public static byte[] CompressQuaternionToBytes(Quaternion q, int bitsPerComponent = 8)
        {
            // Compress the quaternion to get the index, sign bit, and quantized components
            var (index, signBit, quantizedComponents) = CompressQuaternion(q, bitsPerComponent);

            ValidateQuaternionBits(bitsPerComponent);
            // Calculate the total number of bits
            int totalBits = 2 + 1 + 3 * bitsPerComponent; // index (2 bits) + sign bit (1 bit) + 3 components (N bits each)

            // Calculate the number of bytes needed
            int totalBytes = (totalBits + 7) / 8; // Round up to the nearest whole byte

            byte[] byteArray = new byte[totalBytes];

            // Pack the bits into a single integer or long
            ulong packedData = 0;

            // Start packing bits from the most significant bit
            int bitPosition = totalBits;

            // Pack the index (2 bits)
            bitPosition -= 2;
            packedData |= ((ulong)index & 0x3) << bitPosition;

            // Pack the sign bit (1 bit)
            bitPosition -= 1;
            packedData |= ((ulong)signBit & 0x1) << bitPosition;

            // Pack the quantized components (3 * N bits)
            for (int j = 0; j < 3; j++)
            {
                bitPosition -= bitsPerComponent;
                packedData |= ((ulong)quantizedComponents[j] & ((1UL << bitsPerComponent) - 1)) << bitPosition;
            }

            // Now, write the packedData into the byte array
            for (int i = 0; i < totalBytes; i++)
            {
                // Extract the byte at position (from most significant byte)
                int shiftAmount = (totalBytes - 1 - i) * 8;
                byteArray[i] = (byte)((packedData >> shiftAmount) & 0xFF);
            }

            return byteArray;
        }

        /// <summary>
        /// Decompresses the quaternion from a byte array back to the quaternion q = [x, y, z, w].
        /// </summary>
        public static Quaternion DecompressQuaternion(byte[] byteArray, int offset = 0, int bitsPerComponent = 8)
        {
            ValidateQuaternionBits(bitsPerComponent);
            ArgumentNullException.ThrowIfNull(byteArray);
            // Calculate the total number of bits
            int totalBits = 2 + 1 + 3 * bitsPerComponent; // index (2 bits) + sign bit (1 bit) + 3 components (N bits each)
            int totalBytes = (totalBits + 7) / 8;
            if (offset < 0 || offset > byteArray.Length - totalBytes)
                throw new ArgumentOutOfRangeException(nameof(offset), "Compressed quaternion bytes are truncated.");

            // Reconstruct the packed data from the byte array
            ulong packedData = 0;

            for (int i = 0; i < totalBytes; i++)
            {
                int shiftAmount = (totalBytes - 1 - i) * 8;
                packedData |= ((ulong)byteArray[offset + i]) << shiftAmount;
            }

            // Now, unpack the data
            int bitPosition = totalBits;

            // Unpack the index (2 bits)
            bitPosition -= 2;
            int index = (int)((packedData >> bitPosition) & 0x3);

            // Unpack the sign bit (1 bit)
            bitPosition -= 1;
            int signBit = (int)((packedData >> bitPosition) & 0x1);

            // Unpack the quantized components (3 * N bits)
            int[] quantizedComponents = new int[3];
            for (int j = 0; j < 3; j++)
            {
                bitPosition -= bitsPerComponent;
                quantizedComponents[j] = (int)((packedData >> bitPosition) & ((1ul << bitsPerComponent) - 1));
            }

            // Now, use the decompressed data to reconstruct the quaternion
            return DecompressQuaternion((index, signBit, quantizedComponents), bitsPerComponent);
        }

    }
}
