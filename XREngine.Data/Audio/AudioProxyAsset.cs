using XREngine.Core.Files;
using YamlDotNet.Serialization;

namespace XREngine.Data;

/// <summary>
/// A small mono copy of an audio file in IMA ADPCM, at <see cref="ImaAdpcmDecoder.ProxySampleRate"/> with 4 bits for each
/// sample (about 11 KB for each second). A player can open it at once and play it until the full decoder of the source
/// reaches a new position. <see cref="AudioProxyBuilder"/> makes it.
/// </summary>
/// <remarks>
/// The proxy refers to its source with <see cref="SourceReference"/>: a portable asset reference (<c>game://</c> or
/// <c>engine://</c>), or a path relative to the folder of the asset that owns the proxy. It never stores a full path of
/// the local machine.
/// </remarks>
public class AudioProxyAsset : XRAsset
{
    /// <summary>
    /// The default number of samples in each block: 1017. A block then has 512 bytes, the standard IMA ADPCM block size of
    /// mono 22050 Hz WAV files. This gives 11,101 bytes for each second, and a seek decodes at most 1017 samples.
    /// </summary>
    public const int DefaultBlockSamples = 1017;

    private byte[] _encodedData = [];
    private int _sampleRate = ImaAdpcmDecoder.ProxySampleRate;
    private int _blockSamples = DefaultBlockSamples;
    private long _frameCount;
    private string _sourceReference = string.Empty;
    private ulong _sourceContentHash;

    /// <summary>The IMA ADPCM blocks. YAML stores them as one base64 value.</summary>
    [YamlConverter(typeof(Base64ByteArrayYamlTypeConverter))]
    public byte[] EncodedData
    {
        get => _encodedData;
        set => SetField(ref _encodedData, value ?? []);
    }

    /// <summary>The sample rate, in frames per second.</summary>
    public int SampleRate
    {
        get => _sampleRate;
        set => SetField(ref _sampleRate, value);
    }

    /// <summary>The number of samples in each full block. It is odd.</summary>
    public int BlockSamples
    {
        get => _blockSamples;
        set => SetField(ref _blockSamples, value);
    }

    /// <summary>The number of mono frames.</summary>
    public long FrameCount
    {
        get => _frameCount;
        set => SetField(ref _frameCount, value);
    }

    /// <summary>
    /// The stable reference of the source audio file: a portable asset reference, or a path relative to the folder of the
    /// owning asset, with <c>/</c> separators. It is not a full local path.
    /// </summary>
    public string SourceReference
    {
        get => _sourceReference;
        set => SetField(ref _sourceReference, value ?? string.Empty);
    }

    /// <summary>
    /// The XxHash64 of the bytes of the source file when the proxy was made, or zero if it is not known.
    /// A bake makes the proxy again when the source has another hash.
    /// </summary>
    public ulong SourceContentHash
    {
        get => _sourceContentHash;
        set => SetField(ref _sourceContentHash, value);
    }

    /// <summary>The length of the proxy in seconds.</summary>
    public double DurationSeconds
        => SampleRate > 0 ? (double)FrameCount / SampleRate : 0.0;

    /// <summary>Opens a decoder of the proxy. The decoder reads <see cref="EncodedData"/> and does not copy it.</summary>
    /// <exception cref="InvalidDataException">The data does not agree with the block size or the frame count.</exception>
    public ImaAdpcmDecoder CreateDecoder()
    {
        try
        {
            return new ImaAdpcmDecoder(EncodedData, BlockSamples, FrameCount, SampleRate);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"The audio proxy of '{SourceReference}' is not valid: {ex.Message}", ex);
        }
    }
}
