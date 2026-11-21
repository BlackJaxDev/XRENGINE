using System.Globalization;

namespace XREngine.Editor.CutsceneRecording;

/// <summary>
/// Builds the ffmpeg encoder arguments for the cutscene videos. The master video keeps high quality for review. The
/// optimized file size copy uses a fixed bit rate so that its file stays below a size limit.
/// </summary>
internal static class CutsceneVideoEncoderArguments
{
    /// <summary>The ffmpeg encoder name for <see cref="ECutsceneVideoEncoder.Nvenc"/>.</summary>
    public const string NvencCodec = "h264_nvenc";

    /// <summary>The ffmpeg encoder name for <see cref="ECutsceneVideoEncoder.X264"/>.</summary>
    public const string X264Codec = "libx264";

    /// <summary>
    /// Returns the encoder arguments for the high-quality master video. NVENC uses constant-quality VBR with the
    /// slowest preset. libx264 uses CRF 16 with the slow preset.
    /// </summary>
    public static string[] ForMaster(ECutsceneVideoEncoder encoder)
        => encoder switch
        {
            ECutsceneVideoEncoder.Nvenc =>
            [
                "-c:v", NvencCodec,
                "-preset", "p7",
                "-tune", "hq",
                "-profile:v", "high",
                "-rc", "vbr",
                "-cq", "17",
                "-b:v", "0",
                "-spatial-aq", "1",
                "-temporal-aq", "1",
                "-bf", "3",
            ],
            ECutsceneVideoEncoder.X264 =>
            [
                "-c:v", X264Codec,
                "-preset", "slow",
                "-crf", "16",
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unknown cutscene video encoder."),
        };

    /// <summary>
    /// Returns the NVENC arguments for a size-limited video at <paramref name="targetBitrateKbps"/>. NVENC uses its
    /// full-resolution multipass mode in one ffmpeg run, so it needs no pass log files.
    /// </summary>
    public static string[] ForNvencBitrate(int targetBitrateKbps)
    {
        int maxRateKbps = (int)MathF.Ceiling(targetBitrateKbps * 1.12f);
        int bufferKbps = targetBitrateKbps * 2;
        return
        [
            "-c:v", NvencCodec,
            "-preset", "p7",
            "-tune", "hq",
            "-profile:v", "high",
            "-multipass", "fullres",
            "-rc", "vbr",
            "-b:v", $"{targetBitrateKbps.ToString(CultureInfo.InvariantCulture)}k",
            "-maxrate", $"{maxRateKbps.ToString(CultureInfo.InvariantCulture)}k",
            "-bufsize", $"{bufferKbps.ToString(CultureInfo.InvariantCulture)}k",
            "-spatial-aq", "1",
            "-temporal-aq", "1",
            "-bf", "3",
        ];
    }

    /// <summary>
    /// Parses a <c>--video-encoder</c> value. Accepted values are <c>nvenc</c>, <c>x264</c>, and <c>libx264</c>.
    /// </summary>
    public static bool TryParse(string value, out ECutsceneVideoEncoder encoder)
    {
        if (string.Equals(value, "nvenc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, NvencCodec, StringComparison.OrdinalIgnoreCase))
        {
            encoder = ECutsceneVideoEncoder.Nvenc;
            return true;
        }

        if (string.Equals(value, "x264", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, X264Codec, StringComparison.OrdinalIgnoreCase))
        {
            encoder = ECutsceneVideoEncoder.X264;
            return true;
        }

        encoder = ECutsceneVideoEncoder.Nvenc;
        return false;
    }
}
