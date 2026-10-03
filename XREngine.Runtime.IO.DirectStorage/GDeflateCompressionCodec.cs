using Silk.NET.Core.Native;
using Silk.NET.DirectStorage;
using XREngine.Data;

namespace XREngine.Core.Files;

/// <summary>DirectStorage GDeflate codec installed by desktop composition.</summary>
public sealed class GDeflateCompressionCodec : IGDeflateCodec
{
        private sealed class GDeflateCodecContext(DStorage api, ComPtr<IDStorageCompressionCodec> codec) : IDisposable
        {
            public DStorage Api { get; } = api;
            public ComPtr<IDStorageCompressionCodec> Codec { get; } = codec;
            public object SyncRoot { get; } = new();

            public void Dispose()
            {
                Codec.Dispose();
                Api.Dispose();
            }
        }

        private static readonly Lazy<GDeflateCodecContext?> GDeflateCodec = new(CreateGDeflateCodec, LazyThreadSafetyMode.ExecutionAndPublication);

        private static unsafe GDeflateCodecContext? CreateGDeflateCodec()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            try
            {
                DStorage api = DStorage.GetApi();
                ComPtr<IDStorageCompressionCodec> codec = api.CreateCompressionCodec<IDStorageCompressionCodec>((CompressionFormat)1, 0);
                if (codec.Handle is null)
                {
                    api.Dispose();
                    return null;
                }

                return new GDeflateCodecContext(api, codec);
            }
            catch
            {
                return null;
            }
        }

        public unsafe bool TryCompress(ReadOnlySpan<byte> source, out byte[] encoded)
        {
            encoded = [];
            if (source.IsEmpty)
                return true;

            GDeflateCodecContext? context = GDeflateCodec.Value;
            if (context is null)
                return false;

            lock (context.SyncRoot)
            {
                try
                {
                    nuint bound = context.Codec.CompressBufferBound((nuint)source.Length);
                    if (bound == 0 || bound > int.MaxValue)
                        return false;

                    encoded = new byte[(int)bound];
                    nuint compressedSize = 0;

                    int hr;
                    fixed (byte* sourcePtr = source)
                    fixed (byte* encodedPtr = encoded)
                    {
                        hr = context.Codec.CompressBuffer(
                            sourcePtr,
                            (nuint)source.Length,
                            (Silk.NET.DirectStorage.Compression)1,
                            encodedPtr,
                            (nuint)encoded.Length,
                            &compressedSize);
                    }

                    if (hr < 0 || compressedSize == 0 || compressedSize > (nuint)encoded.Length)
                    {
                        encoded = [];
                        return false;
                    }

                    if (compressedSize != (nuint)encoded.Length)
                        Array.Resize(ref encoded, (int)compressedSize);

                    return true;
                }
                catch
                {
                    encoded = [];
                    return false;
                }
            }
        }

        public unsafe bool TryDecompress(ReadOnlySpan<byte> encodedSource, int expectedDecodedLength, out byte[] decoded)
        {
            decoded = [];
            if (expectedDecodedLength < 0)
                return false;

            if (expectedDecodedLength == 0)
                return encodedSource.IsEmpty;

            if (encodedSource.IsEmpty)
                return false;

            GDeflateCodecContext? context = GDeflateCodec.Value;
            if (context is null)
                return false;

            lock (context.SyncRoot)
            {
                try
                {
                    decoded = new byte[expectedDecodedLength];
                    nuint actualDecodedSize = 0;

                    int hr;
                    fixed (byte* encodedPtr = encodedSource)
                    fixed (byte* decodedPtr = decoded)
                    {
                        hr = context.Codec.DecompressBuffer(
                            encodedPtr,
                            (nuint)encodedSource.Length,
                            decodedPtr,
                            (nuint)decoded.Length,
                            &actualDecodedSize);
                    }

                    if (hr < 0 || actualDecodedSize != (nuint)expectedDecodedLength)
                    {
                        decoded = [];
                        return false;
                    }

                    return true;
                }
                catch
                {
                    decoded = [];
                    return false;
                }
            }
        }

}
