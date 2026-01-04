using System.Numerics;
using System.Text;
using XREngine.Data.Transforms.Rotations;

namespace XREngine.Data
{
    public static class Compression
    {
        public static byte[] DecompressFromString(uint? length, string byteStr)
        {
            if (string.IsNullOrEmpty(byteStr))
                return [];

            // YAML scalars may include newlines/whitespace (folded or literal blocks) for very long strings.
            // Our compressor emits a contiguous hex string, so strip whitespace before validating/decoding.
            // Also accept an optional "0x" prefix for debugging convenience.
            if (byteStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                byteStr = byteStr[2..];

            bool hasWhitespace = false;
            for (int i = 0; i < byteStr.Length; i++)
            {
                if (char.IsWhiteSpace(byteStr[i]))
                {
                    hasWhitespace = true;
                    break;
                }
            }

            if (hasWhitespace)
            {
                var sb = new StringBuilder(byteStr.Length);
                foreach (char c in byteStr)
                {
                    if (!char.IsWhiteSpace(c))
                        sb.Append(c);
                }
                byteStr = sb.ToString();
            }

            static bool TryDecodeAndDecompress(uint? expectedLength, string hex, out byte[] decompressed)
            {
                decompressed = [];
                byte[] compressed;
                try
                {
                    compressed = Convert.FromHexString(hex);
                }
                catch
                {
                    return false;
                }

                try
                {
                    decompressed = Decompress(compressed, false);
                    return expectedLength is null || decompressed.Length == expectedLength.Value;
                }
                catch
                {
                    decompressed = [];
                    return false;
                }
            }

            if ((byteStr.Length & 1) != 0)
            {
                // Best-effort recovery: if a single hex nibble was lost (common when huge YAML scalars
                // are edited/wrapped), try restoring by padding either the front or end.
                // If the caller provided an expected decompressed length, validate against it.
                if (TryDecodeAndDecompress(length, "0" + byteStr, out var recoveredA))
                    return recoveredA;
                if (TryDecodeAndDecompress(length, byteStr + "0", out var recoveredB))
                    return recoveredB;

                // If the string was truncated by exactly 1 character, another plausible repair is to
                // drop a nibble from either end to re-align to whole bytes.
                if (byteStr.Length > 1)
                {
                    if (TryDecodeAndDecompress(length, byteStr[..^1], out var recoveredC))
                        return recoveredC;
                    if (TryDecodeAndDecompress(length, byteStr[1..], out var recoveredD))
                        return recoveredD;
                }

                // If no expected length was provided, fall back to accepting the first successful decode+decompress.
                if (length is null)
                {
                    if (TryDecodeAndDecompress(null, "0" + byteStr, out recoveredA))
                        return recoveredA;
                    if (TryDecodeAndDecompress(null, byteStr + "0", out recoveredB))
                        return recoveredB;

                    if (byteStr.Length > 1)
                    {
                        if (TryDecodeAndDecompress(null, byteStr[..^1], out var recoveredC))
                            return recoveredC;
                        if (TryDecodeAndDecompress(null, byteStr[1..], out var recoveredD))
                            return recoveredD;
                    }
                }

                // Provide a more actionable error: this is almost certainly a corrupted/truncated payload.
                // Include a small preview to help spot obvious formatting issues.
                string head = byteStr.Length <= 32 ? byteStr : byteStr[..32];
                string tail = byteStr.Length <= 32 ? string.Empty : byteStr[^32..];
                throw new FormatException(
                    $"Compressed byte string must contain an even number of hex characters (after trimming). Got {byteStr.Length}. " +
                    $"Tried repairs (pad front/back, drop first/last nibble) but none produced a valid LZMA payload" +
                    (length is null ? "." : $" matching expected length {length.Value}.") +
                    $" Head='{head}' Tail='{tail}'.");
            }

            byte[] compressed;
            try
            {
                compressed = Convert.FromHexString(byteStr);
            }
            catch (FormatException ex)
            {
                throw new FormatException("Compressed byte string contains invalid hexadecimal characters.", ex);
            }
            return Decompress(compressed, false);
        }

        public static unsafe byte[] Compress(byte[] arr, bool longSize = false)
        {
            SevenZip.Compression.LZMA.Encoder encoder = new();
            using MemoryStream inStream = new(arr);
            using MemoryStream outStream = new();
            encoder.WriteCoderProperties(outStream);
            if (longSize)
            {
                outStream.Write(BitConverter.GetBytes(arr.LongLength), 0, 8);
                encoder.Code(inStream, outStream, arr.LongLength, -1, null);
            }
            else
            {
                outStream.Write(BitConverter.GetBytes(arr.Length), 0, 4);
                encoder.Code(inStream, outStream, arr.Length, -1, null);
            }
            return outStream.ToArray();
        }
        /// <summary>
        /// Compresses a byte array using the LZMA algorithm, without allocating new objects.
        /// </summary>
        /// <param name="arr"></param>
        /// <param name="encoder"></param>
        /// <param name="inStreamObject"></param>
        /// <param name="outStreamObject"></param>
        /// <returns></returns>
        public static unsafe byte[] Compress(
            byte[] arr,
            ref SevenZip.Compression.LZMA.Encoder encoder,
            ref MemoryStream inStreamObject,
            ref MemoryStream outStreamObject)
        {
            encoder ??= new();
            inStreamObject ??= new();
            outStreamObject ??= new();

            if (inStreamObject.Length < arr.Length)
                inStreamObject.SetLength(arr.Length);
            inStreamObject.Seek(0, SeekOrigin.Begin);
            inStreamObject.Write(arr, 0, arr.Length);
            inStreamObject.Seek(0, SeekOrigin.Begin);

            if (outStreamObject.Length < arr.Length)
                outStreamObject.SetLength(arr.Length); // Set the length of the output stream to the size of the input stream, will be smaller after compression
            outStreamObject.Seek(0, SeekOrigin.Begin);

            encoder.WriteCoderProperties(outStreamObject);
            outStreamObject.Write(BitConverter.GetBytes(arr.Length), 0, 4);
            encoder.Code(inStreamObject, outStreamObject, arr.Length, -1, null);
            return outStreamObject.ToArray();
        }
        public static int Decompress(byte[] inBuf, int inOffset, int inLength, byte[] outBuf, int outOffset)
        {
            SevenZip.Compression.LZMA.Decoder decoder = new();
            using MemoryStream inStream = new(inBuf, inOffset, inLength);
            using MemoryStream outStream = new(outBuf, outOffset, outBuf.Length - outOffset);

            inStream.Seek(0, SeekOrigin.Begin);
            byte[] properties = new byte[5];
            inStream.Read(properties, 0, 5);
            byte[] lengthBytes = new byte[4];
            inStream.Read(lengthBytes, 0, 4);

            decoder.SetDecoderProperties(properties);
            int len = BitConverter.ToInt32(lengthBytes, 0);

            decoder.Code(inStream, outStream, inStream.Length - 9, len, null);

            return len;
        }
        public static int Decompress(
            byte[] inBuf,
            int inOffset,
            int inLength,
            byte[] outBuf,
            int outOffset,
            ref SevenZip.Compression.LZMA.Decoder decoder,
            ref MemoryStream inStreamObject,
            ref MemoryStream outStreamObject)
        {
            decoder ??= new();
            inStreamObject ??= new();
            outStreamObject ??= new();

            if (inStreamObject.Length < inLength)
                inStreamObject.SetLength(inLength);
            inStreamObject.Seek(0, SeekOrigin.Begin);
            inStreamObject.Write(inBuf, inOffset, inLength);
            inStreamObject.Seek(0, SeekOrigin.Begin);

            //if (outStreamObject.Length < outBuf.Length - outOffset)
            //outStreamObject.SetLength(outBuf.Length - outOffset);
            outStreamObject.SetLength(0);
            outStreamObject.Seek(0, SeekOrigin.Begin);

            byte[] properties = new byte[5];
            inStreamObject.Read(properties, 0, 5);
            byte[] lengthBytes = new byte[4];
            inStreamObject.Read(lengthBytes, 0, 4);

            decoder.SetDecoderProperties(properties);
            int len = BitConverter.ToInt32(lengthBytes, 0);

            decoder.Code(inStreamObject, outStreamObject, inStreamObject.Length - 9, len, null);

            outStreamObject.Seek(0, SeekOrigin.Begin);
            outStreamObject.Read(outBuf, outOffset, len);

            return len;
        }
        public static byte[] Decompress(byte[] bytes, bool longLength = false)
        {
            SevenZip.Compression.LZMA.Decoder decoder = new();
            using MemoryStream inStream = new(bytes);
            using MemoryStream outStream = new();

            inStream.Seek(0, SeekOrigin.Begin);
            byte[] properties = new byte[5];
            inStream.Read(properties, 0, 5);

            int sizeByteCount = longLength ? 8 : 4;
            byte[] lengthBytes = new byte[sizeByteCount];
            inStream.Read(lengthBytes, 0, sizeByteCount);

            decoder.SetDecoderProperties(properties);
            long len = longLength
                ? BitConverter.ToInt64(lengthBytes, 0)
                : BitConverter.ToInt32(lengthBytes, 0);

            decoder.Code(inStream, outStream, inStream.Length - 5 - sizeByteCount, len, null);

            return outStream.ToArray();
        }

        public static unsafe string CompressToString(DataSource source)
        {
            if (source.Length == 0)
                return string.Empty;

            int length = checked((int)source.Length);
            ReadOnlySpan<byte> bytes = new((void*)source.Address, length);
            return CompressToString(bytes.ToArray());
        }

        public static unsafe string CompressToString(byte[] arr)
        {
            byte[] compressed = Compress(arr, false);
            return Convert.ToHexString(compressed);
        }

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

        public static byte[] Compress(Vector3 value, out int bits)
            => FloatQuantizer.QuantizeToByteArray([value.X, value.Y, value.Z], out bits);

        public static byte[] Compress(Rotator value, out int bits)
            => FloatQuantizer.QuantizeToByteArray([value.Pitch, value.Yaw, value.Roll], out bits);

        public static byte[] Compress(float value, out int bits)
            => FloatQuantizer.QuantizeToByteArray([value], out bits);
        
        public static byte[] Compress(int value, out int bits)
        {
            bool sign = value < 0;
            value = Math.Abs(value);
            bits = 1;
            //Calculate the number of bits required to store the integer
            while (value > 0)
            {
                value >>= 1;
                bits++;
            }
            byte[] bytes = new byte[(bits + 7) / 8];
            for (int j = 0; j < bytes.Length; j++)
            {
                bytes[j] = (byte)(value & 0xFF);
                value >>= 8;
            }
            bytes[^1] |= (byte)(sign ? 0x80 : 0);
            return bytes;
        }
    }
}