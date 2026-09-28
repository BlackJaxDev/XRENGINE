using System.Numerics;

namespace XREngine.Data
{
    public static partial class Compression
    {
        /// <summary>
        /// Compresses a unit quaternion q = [w, x, y, z] into a compressed form.
        /// N is the number of bits per component for the quantized components.
        /// </summary>
        public static (int index, int signBit, int[] quantizedComponents) CompressQuaternion(Quaternion q, int bitsPerComponent = 8)
        {
            // Find the index of the largest component
            float[] absQ = Array.ConvertAll([q.X, q.Y, q.Z, q.W], MathF.Abs);
            int i = Array.IndexOf(absQ, MaxValue(absQ));

            // Get the sign of q_i
            int signBit = q[i] >= 0 ? 0 : 1;

            // Remove q_i to get the remaining components
            var qRest = new float[3];
            int restIndex = 0;
            for (int idx = 0; idx < 4; idx++)
                if (idx != i)
                    qRest[restIndex++] = q[idx];

            // Compute the scaling factor
            float scale = MathF.Sqrt(1 - q[i] * q[i]);
            float[] scaledComponents = scale > 0 ? Array.ConvertAll(qRest, qi => qi / scale) : ([0.0f, 0.0f, 0.0f]);

            // Quantize the scaled components
            int[] quantizedComponents = new int[3];
            int maxInt = (1 << bitsPerComponent) - 1; // 2^N - 1
            for (int j = 0; j < 3; j++)
            {
                // Map from [-1, 1] to [0, maxInt]
                int qc = (int)MathF.Round((scaledComponents[j] + 1) * (maxInt / 2.0f));
                qc = Math.Max(0, Math.Min(maxInt, qc)); // Clamp to [0, maxInt]
                quantizedComponents[j] = qc;
            }

            // Pack the data
            return (i, signBit, quantizedComponents);
        }

        public static Quaternion DecompressQuaternion((int index, int signBit, int[] quantizedComponents) compressedData, int bitsPerComponent = 8)
        {
            int i = compressedData.index;
            int signBit = compressedData.signBit;
            int[] quantizedComponents = compressedData.quantizedComponents;
            int maxInt = (1 << bitsPerComponent) - 1; // 2^N - 1

            // Dequantize the components
            float[] scaledComponents = new float[3];
            for (int j = 0; j < 3; j++)
            {
                float sc = (quantizedComponents[j] / (maxInt / 2.0f)) - 1;
                sc = Math.Max(-1.0f, Math.Min(1.0f, sc)); // Clamp to [-1, 1]
                scaledComponents[j] = sc;
            }

            // Compute q_i
            float sumOfSquares = scaledComponents.Sum(x => x * x);
            float q_i = MathF.Sqrt(Math.Max(0.0f, 1.0f - sumOfSquares));
            if (signBit == 1)
                q_i = -q_i;

            // Rescale the components
            float scale = MathF.Sqrt(1.0f - q_i * q_i);
            float[] qRest = scale > 0 ? Array.ConvertAll(scaledComponents, sc => sc * scale) : ([0.0f, 0.0f, 0.0f]);

            // Reconstruct the quaternion
            Quaternion q = new();
            int restIndex = 0;
            for (int idx = 0; idx < 4; idx++)
                q[idx] = idx == i ? q_i : qRest[restIndex++];

            return q;
        }

        /// <summary>
        /// Utility method to find the maximum value in a double array.
        /// </summary>
        private static float MaxValue(float[] array)
        {
            float max = array[0];
            foreach (var val in array)
                if (val > max)
                    max = val;
            return max;
        }

        /// <summary>
        /// Compresses a unit quaternion q = [w, x, y, z] into a byte array.
        /// N is the number of bits per component for the quantized components.
        /// </summary>
        public static byte[] CompressQuaternionToBytes(Quaternion q, int bitsPerComponent = 8)
        {
            // Compress the quaternion to get the index, sign bit, and quantized components
            var (index, signBit, quantizedComponents) = CompressQuaternion(q, bitsPerComponent);

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
        /// Decompresses the quaternion from a byte array back to the quaternion q = [w, x, y, z].
        /// </summary>
        public static Quaternion DecompressQuaternion(byte[] byteArray, int offset = 0, int bitsPerComponent = 8)
        {
            // Calculate the total number of bits
            int totalBits = 2 + 1 + 3 * bitsPerComponent; // index (2 bits) + sign bit (1 bit) + 3 components (N bits each)
            int totalBytes = (totalBits + 7) / 8;

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
