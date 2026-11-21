using System.Diagnostics;
using System.Globalization;

namespace XREngine.Editor.CutsceneRecording;

/// <summary>
/// Writes the files that a recording makes from its master video with ffmpeg: the optimized file size copy and the
/// contact sheet.
/// </summary>
internal static class CutsceneVideoOutputs
{
    /// <summary>The number of encodes that the optimized file size copy tries before it fails.</summary>
    private const int OptimizedEncodeAttempts = 3;

    /// <summary>
    /// The first bit rate aims at this fraction of the size limit, so the container overhead and the encoder rate
    /// variation still fit below the limit.
    /// </summary>
    private const double OptimizedTargetFraction = 0.787;

    /// <summary>
    /// Encodes <paramref name="sourceMp4Path"/> again into a video that stays strictly below <paramref name="maxBytes"/>.
    /// The first bit rate aims at <see cref="OptimizedTargetFraction"/> of the limit over the clip duration. When a result
    /// is still too large, the method lowers the bit rate and tries again, up to <see cref="OptimizedEncodeAttempts"/>
    /// times, and then throws.
    /// </summary>
    public static async Task EncodeOptimizedFileSizeAsync(
        string ffmpegPath,
        ECutsceneVideoEncoder encoder,
        string sourceMp4Path,
        double durationSeconds,
        long maxBytes,
        string outputMp4Path,
        CancellationToken cancellationToken)
    {
        int bitrateKbps = Math.Clamp(
            (int)Math.Floor((maxBytes * OptimizedTargetFraction * 8.0 / 1000.0) / Math.Max(durationSeconds, 1.0)),
            100,
            50_000);

        long encodedBytes = 0L;
        for (int attempt = 1; attempt <= OptimizedEncodeAttempts; attempt++)
        {
            if (encoder == ECutsceneVideoEncoder.Nvenc)
                await RunNvencBitrateEncodeAsync(ffmpegPath, sourceMp4Path, bitrateKbps, outputMp4Path, cancellationToken).ConfigureAwait(false);
            else
                await RunTwoPassX264EncodeAsync(ffmpegPath, sourceMp4Path, bitrateKbps, outputMp4Path, cancellationToken).ConfigureAwait(false);

            encodedBytes = new FileInfo(outputMp4Path).Length;
            if (encodedBytes < maxBytes)
                return;

            Console.WriteLine(
                $"Optimized file size encode attempt {attempt} produced {encodedBytes} bytes at {bitrateKbps} kbps; " +
                $"the limit is {maxBytes} bytes.");
            bitrateKbps = Math.Max(100, (int)(bitrateKbps * 0.72f));
        }

        throw new InvalidOperationException(
            $"The optimized file size copy '{outputMp4Path}' is still {encodedBytes} bytes after {OptimizedEncodeAttempts} attempts; " +
            $"it must be smaller than {maxBytes} bytes.");
    }

    private static Task RunNvencBitrateEncodeAsync(
        string ffmpegPath,
        string sourceMp4Path,
        int targetBitrateKbps,
        string outputMp4Path,
        CancellationToken cancellationToken)
    {
        if (File.Exists(outputMp4Path))
            File.Delete(outputMp4Path);

        List<string> arguments = ["-hide_banner", "-loglevel", "error", "-y", "-i", sourceMp4Path];
        arguments.AddRange(CutsceneVideoEncoderArguments.ForNvencBitrate(targetBitrateKbps));
        arguments.AddRange(["-pix_fmt", "yuv420p", "-movflags", "+faststart", outputMp4Path]);
        return RunFfmpegArgumentsAsync(ffmpegPath, arguments, cancellationToken);
    }

    private static async Task RunTwoPassX264EncodeAsync(
        string ffmpegPath,
        string sourceMp4Path,
        int targetBitrateKbps,
        string outputMp4Path,
        CancellationToken cancellationToken)
    {
        if (File.Exists(outputMp4Path))
            File.Delete(outputMp4Path);

        string passLogDir = Path.Combine(Path.GetDirectoryName(outputMp4Path) ?? Path.GetTempPath(), "_passlog");
        DeleteDirectoryIfPresent(passLogDir);
        Directory.CreateDirectory(passLogDir);
        string passLogPrefix = Path.Combine(passLogDir, "ffmpeg2pass");
        int maxRateKbps = (int)MathF.Ceiling(targetBitrateKbps * 1.12f);
        int bufSizeKbps = targetBitrateKbps * 2;
        string nullSink = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

        try
        {
            await RunFfmpegArgumentsAsync(
                ffmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-i", sourceMp4Path,
                    "-c:v", CutsceneVideoEncoderArguments.X264Codec,
                    "-preset", "slow",
                    "-b:v", $"{targetBitrateKbps}k",
                    "-maxrate", $"{maxRateKbps}k",
                    "-bufsize", $"{bufSizeKbps}k",
                    "-pix_fmt", "yuv420p",
                    "-pass", "1",
                    "-passlogfile", passLogPrefix,
                    "-f", "mp4",
                    nullSink,
                ],
                cancellationToken).ConfigureAwait(false);

            await RunFfmpegArgumentsAsync(
                ffmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-i", sourceMp4Path,
                    "-c:v", CutsceneVideoEncoderArguments.X264Codec,
                    "-preset", "slow",
                    "-b:v", $"{targetBitrateKbps}k",
                    "-maxrate", $"{maxRateKbps}k",
                    "-bufsize", $"{bufSizeKbps}k",
                    "-pix_fmt", "yuv420p",
                    "-movflags", "+faststart",
                    "-pass", "2",
                    "-passlogfile", passLogPrefix,
                    outputMp4Path,
                ],
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteDirectoryIfPresent(passLogDir);
        }
    }

    /// <summary>
    /// Writes one contact sheet PNG from evenly spaced frames of <paramref name="videoPath"/>. The sheet has
    /// <paramref name="columns"/> columns of <paramref name="thumbnailWidth"/>-pixel thumbnails and at most
    /// <paramref name="maxThumbnails"/> thumbnails, so a reviewer can check every shot in one image.
    /// </summary>
    public static Task WriteContactSheetAsync(
        string ffmpegPath,
        string videoPath,
        int totalFrames,
        int columns,
        int thumbnailWidth,
        int maxThumbnails,
        string contactSheetPath,
        CancellationToken cancellationToken)
    {
        int frameStep = Math.Max(1, (totalFrames + maxThumbnails - 1) / maxThumbnails);
        int thumbnailCount = (totalFrames + frameStep - 1) / frameStep;
        int rows = (thumbnailCount + columns - 1) / columns;
        string filter = string.Create(
            CultureInfo.InvariantCulture,
            $"select='not(mod(n\\,{frameStep}))',scale={thumbnailWidth}:-2,tile={columns}x{rows}");

        if (File.Exists(contactSheetPath))
            File.Delete(contactSheetPath);

        return RunFfmpegArgumentsAsync(
            ffmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", videoPath,
                "-vf", filter,
                "-frames:v", "1",
                contactSheetPath,
            ],
            cancellationToken);
    }

    public static void DeleteDirectoryIfPresent(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static async Task RunFfmpegArgumentsAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        for (int i = 0; i < arguments.Count; i++)
            startInfo.ArgumentList.Add(arguments[i]);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{ffmpegPath}'.");
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {stderr}");
    }
}
