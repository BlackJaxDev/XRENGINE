using XREngine.Rendering;

namespace XREngine.Runtime.Imaging.Bc7Enc;

/// <summary>
/// Texture block encoder that calls the bc7enc_rdo encoders through the native xre_bcn shim.
/// Large images are split into strips of block rows that encode in parallel.
/// </summary>
internal sealed class Bc7EncTextureBlockEncoder : ITextureBlockEncoder
{
    /// <summary>BC7 uber level 0 is the fastest mode search; the cook runs offline, but texture counts are large.</summary>
    private const uint Bc7UberLevel = 0;

    /// <summary>Block rows per parallel work item. Small images encode as one item.</summary>
    private const int BlockRowsPerStrip = 8;

    private Bc7EncTextureBlockEncoder(string description)
        => Description = description;

    public string Description { get; }

    /// <summary>
    /// Loads the native library, checks its ABI version, and builds the encoder tables.
    /// </summary>
    internal static bool TryCreate(out Bc7EncTextureBlockEncoder? encoder, out string failure)
    {
        encoder = null;
        try
        {
            int apiVersion = NativeBcnEncoder.ApiVersion();
            if (apiVersion != NativeBcnEncoder.ExpectedApiVersion)
            {
                failure = $"the native xre_bcn library has ABI version {apiVersion}, but version {NativeBcnEncoder.ExpectedApiVersion} is required. Run Tools/Dependencies/Build-Bc7Enc.ps1 -ForceBuild.";
                return false;
            }

            NativeBcnEncoder.Initialize();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            failure = ex.Message;
            return false;
        }

        string version = NativeBcnEncoder.VersionText;
        encoder = new Bc7EncTextureBlockEncoder(string.IsNullOrEmpty(version) ? "xre_bcn (bc7enc_rdo)" : version);
        failure = string.Empty;
        return true;
    }

    public bool TryEncodeBc7(ReadOnlySpan<byte> rgba, uint width, uint height, bool perceptual, Span<byte> output, out string failure)
        => TryEncode(rgba, width, height, output, 16, new EncodeRequest(EBlockCodec.Bc7, perceptual ? 1u : 0u, 0u), out failure);

    public bool TryEncodeBc4(ReadOnlySpan<byte> rgba, uint width, uint height, uint channel, Span<byte> output, out string failure)
        => TryEncode(rgba, width, height, output, 8, new EncodeRequest(EBlockCodec.Bc4, channel, 0u), out failure);

    public bool TryEncodeBc5(ReadOnlySpan<byte> rgba, uint width, uint height, uint channel0, uint channel1, Span<byte> output, out string failure)
        => TryEncode(rgba, width, height, output, 16, new EncodeRequest(EBlockCodec.Bc5, channel0, channel1), out failure);

    private enum EBlockCodec
    {
        Bc7,
        Bc4,
        Bc5,
    }

    private readonly record struct EncodeRequest(EBlockCodec Codec, uint Argument0, uint Argument1);

    private static unsafe bool TryEncode(
        ReadOnlySpan<byte> rgba,
        uint width,
        uint height,
        Span<byte> output,
        int blockBytes,
        EncodeRequest request,
        out string failure)
    {
        if (width == 0 || height == 0)
        {
            failure = "the image has no pixels";
            return false;
        }

        long pixelBytes = (long)width * height * 4;
        if (rgba.Length < pixelBytes)
        {
            failure = $"the RGBA8 input has {rgba.Length} bytes, but {width}x{height} needs {pixelBytes}";
            return false;
        }

        long blocksX = (width + 3) / 4;
        long blocksY = (height + 3) / 4;
        long outputBytes = blocksX * blocksY * blockBytes;
        if (output.Length < outputBytes)
        {
            failure = $"the output has {output.Length} bytes, but {outputBytes} are required";
            return false;
        }

        int stripCount = (int)((blocksY + BlockRowsPerStrip - 1) / BlockRowsPerStrip);
        int firstError = NativeBcnEncoder.ResultOk;
        fixed (byte* rgbaPointer = rgba)
        fixed (byte* outputPointer = output)
        {
            nint source = (nint)rgbaPointer;
            nint destination = (nint)outputPointer;
            if (stripCount == 1)
            {
                firstError = EncodeStrip(source, destination, width, height, blocksX, blockBytes, 0, request);
            }
            else
            {
                // Cook-time work: the closure allocation is acceptable here.
                Parallel.For(0, stripCount, strip =>
                {
                    int result = EncodeStrip(source, destination, width, height, blocksX, blockBytes, strip, request);
                    if (result != NativeBcnEncoder.ResultOk)
                        Interlocked.CompareExchange(ref firstError, result, NativeBcnEncoder.ResultOk);
                });
            }
        }

        if (firstError != NativeBcnEncoder.ResultOk)
        {
            failure = $"the native {request.Codec} encoder failed: {NativeBcnEncoder.DescribeResult(firstError)}";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static unsafe int EncodeStrip(
        nint rgba,
        nint output,
        uint width,
        uint height,
        long blocksX,
        int blockBytes,
        int strip,
        EncodeRequest request)
    {
        uint firstPixelRow = (uint)(strip * BlockRowsPerStrip * 4);
        uint stripHeight = Math.Min(height - firstPixelRow, (uint)(BlockRowsPerStrip * 4));
        byte* source = (byte*)rgba + (long)firstPixelRow * width * 4;
        long stripBlockRows = (stripHeight + 3) / 4;
        byte* destination = (byte*)output + (long)strip * BlockRowsPerStrip * blocksX * blockBytes;
        nuint destinationSize = (nuint)(stripBlockRows * blocksX * blockBytes);
        return request.Codec switch
        {
            EBlockCodec.Bc7 => NativeBcnEncoder.EncodeBc7(source, width, stripHeight, Bc7UberLevel, (int)request.Argument0, destination, destinationSize),
            EBlockCodec.Bc4 => NativeBcnEncoder.EncodeBc4(source, width, stripHeight, request.Argument0, destination, destinationSize),
            _ => NativeBcnEncoder.EncodeBc5(source, width, stripHeight, request.Argument0, request.Argument1, destination, destinationSize),
        };
    }
}
