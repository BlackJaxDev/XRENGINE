using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using XREngine.Rendering.Models.Gaussian.Baking.Cli;

namespace XREngine.Rendering.Models.Gaussian.Baking.Video;

/// <summary>
/// Encodes the videos and posters of a preview loop with an external ffmpeg process. Each output has its own process,
/// which gets raw RGBA frames on its standard input. The process starts with an argument list and no shell.
/// </summary>
/// <remarks>
/// The arguments make the output the same for the same frames: one encoder thread, bit-exact flags, and no metadata.
/// H.264 uses libopenh264, or h264_nvenc when the bake asks for NVENC (NVENC output is not guaranteed to be bit-exact).
/// The encoder never selects libx264.
/// </remarks>
public sealed class FfmpegEncoder
{
    /// <summary>The ffmpeg name of the VP9 encoder.</summary>
    public const string Vp9EncoderName = "libvpx-vp9";

    /// <summary>The ffmpeg name of the WebP encoder.</summary>
    public const string WebpEncoderName = "libwebp";

    /// <summary>The ffmpeg name of the OpenH264 encoder.</summary>
    public const string OpenH264EncoderName = "libopenh264";

    /// <summary>The ffmpeg name of the NVIDIA NVENC H.264 encoder.</summary>
    public const string NvencEncoderName = "h264_nvenc";

    /// <summary>The largest denominator that <see cref="FormatFrameRate"/> tries for an exact frame rate.</summary>
    private const int MaxRateDenominator = 1001;

    /// <summary>The denominator of a frame rate that no small fraction gives.</summary>
    private const long FallbackRateDenominator = 1_000_000;

    /// <summary>The longest time that <c>ffmpeg -encoders</c> can take.</summary>
    private static readonly TimeSpan EncoderListTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Creates an encoder.</summary>
    /// <param name="ffmpegPath">The ffmpeg executable: a path, or a command that the system finds on the PATH.</param>
    /// <param name="h264Encoder">
    /// The H.264 encoder: <see cref="BakeSplatPreviewOptions.OpenH264Encoder"/> or <see cref="BakeSplatPreviewOptions.NvencEncoder"/>.
    /// </param>
    public FfmpegEncoder(string ffmpegPath, string h264Encoder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ = H264EncoderNameFor(h264Encoder);
        FfmpegPath = ffmpegPath;
        H264Encoder = h264Encoder;
    }

    /// <summary>Gets the ffmpeg executable.</summary>
    public string FfmpegPath { get; }

    /// <summary>Gets the H.264 encoder option: <see cref="BakeSplatPreviewOptions.OpenH264Encoder"/> or <see cref="BakeSplatPreviewOptions.NvencEncoder"/>.</summary>
    public string H264Encoder { get; }

    /// <summary>
    /// Returns the encoders that the bake needs and that an <c>ffmpeg -hide_banner -encoders</c> listing does not have.
    /// The bake needs <see cref="Vp9EncoderName"/>, the H.264 encoder of <paramref name="h264Encoder"/>
    /// (<see cref="OpenH264EncoderName"/> or <see cref="NvencEncoderName"/>), and <see cref="WebpEncoderName"/>, in this order.
    /// </summary>
    /// <remarks>
    /// An encoder line has six capability flags, a space, the encoder name, and a description, for example
    /// <c>" V....D libvpx-vp9           libvpx VP9 (codec vp9)"</c>. The names must be equal; a name that only starts
    /// with a needed name, such as <c>libwebp_anim</c>, does not count.
    /// </remarks>
    /// <param name="encodersOutput">The standard output of <c>ffmpeg -hide_banner -encoders</c>.</param>
    /// <param name="h264Encoder">The H.264 encoder option.</param>
    public static IReadOnlyList<string> FindMissingEncoders(string encodersOutput, string h264Encoder)
    {
        ArgumentNullException.ThrowIfNull(encodersOutput);
        string h264Name = H264EncoderNameFor(h264Encoder);

        HashSet<string> available = new(StringComparer.Ordinal);
        foreach (ReadOnlySpan<char> line in encodersOutput.AsSpan().EnumerateLines())
        {
            if (TryReadEncoderName(line, out string? name))
                available.Add(name);
        }

        List<string> missing = [];
        foreach (string required in (string[])[Vp9EncoderName, h264Name, WebpEncoderName])
        {
            if (!available.Contains(required))
                missing.Add(required);
        }

        return missing;
    }

    /// <summary>
    /// Runs <c>ffmpeg -hide_banner -encoders</c> and returns the needed encoders that it does not list
    /// (see <see cref="FindMissingEncoders"/>).
    /// </summary>
    /// <exception cref="GaussianBakeException">
    /// ffmpeg cannot start, stops with an error, or takes more than 30 seconds. The failure code is
    /// <see cref="GaussianBakeFailureCodes.EncodeFailed"/>.
    /// </exception>
    public async Task<IReadOnlyList<string>> VerifyEncodersAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EncoderListTimeout);

        using Process process = StartProcess(["-hide_banner", "-encoders"]);
        try
        {
            process.StandardInput.Close();
            Task<string> listing = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await listing.ConfigureAwait(false);
            string stderr = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new GaussianBakeException(
                    GaussianBakeFailureCodes.EncodeFailed,
                    $"'{FfmpegPath} -hide_banner -encoders' stopped with exit code {process.ExitCode}.{FormatStderr(LastLines(stderr))}");
            }

            return FindMissingEncoders(output, H264Encoder);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.EncodeFailed,
                $"'{FfmpegPath} -hide_banner -encoders' did not stop in {EncoderListTimeout.TotalSeconds:F0} seconds.");
        }
        finally
        {
            await StopAsync(process).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the ffmpeg arguments of one output as one line. Each argument is separated by a space, and an argument
    /// with a space or a quote is quoted with the Windows command line rules. <see cref="StartAsync"/> gives the same
    /// arguments to the process as a list.
    /// </summary>
    /// <remarks>
    /// <para>The arguments are, in this order:</para>
    /// <list type="number">
    /// <item>Common: <c>-hide_banner -loglevel error -y -f rawvideo -pix_fmt rgba -s {width}x{height} -r {rate} -i - -an
    /// -threads 1 -fflags +bitexact -flags:v +bitexact -map_metadata -1</c>. The rate comes from <see cref="FormatFrameRate"/>.</item>
    /// <item>When <see cref="LoopRendition.Height"/> is less than <paramref name="height"/>:
    /// <c>-vf scale=-2:{Height}:flags=area</c>.</item>
    /// <item>VP9: <c>-c:v libvpx-vp9 -b:v 0 -crf 34 -deadline good -cpu-used 2 -row-mt 0 -pix_fmt yuv420p</c>.</item>
    /// <item>H.264 with OpenH264: <c>-c:v libopenh264 -b:v {bitrate} -pix_fmt yuv420p -movflags +faststart</c>; with
    /// NVENC: <c>-c:v h264_nvenc -preset p5 -b:v {bitrate} -pix_fmt yuv420p -movflags +faststart</c>. The bit rate is
    /// <c>4M</c> for a height of 1080 or more, and <c>1M</c> for a lower height.</item>
    /// <item>WebP: <c>-frames:v 1 -c:v libwebp -quality 85</c>.</item>
    /// <item>The output path.</item>
    /// </list>
    /// </remarks>
    /// <param name="rendition">The output.</param>
    /// <param name="width">The width of the input frames, in pixels.</param>
    /// <param name="height">The height of the input frames, in pixels.</param>
    /// <param name="fps">The frame rate of the input frames.</param>
    /// <param name="h264Encoder">The H.264 encoder option.</param>
    /// <param name="outputPath">The output file.</param>
    public static string BuildArguments(LoopRendition rendition, int width, int height, float fps, string h264Encoder, string outputPath)
    {
        List<string> arguments = BuildArgumentList(rendition, width, height, fps, h264Encoder, outputPath);
        StringBuilder line = new();
        foreach (string argument in arguments)
        {
            if (line.Length > 0)
                line.Append(' ');
            AppendQuoted(line, argument);
        }

        return line.ToString();
    }

    /// <summary>
    /// Writes a frame rate as an exact fraction for the ffmpeg <c>-r</c> option, so that the video rate is the rate of
    /// the cache and not a rounded decimal. The result is <c>n/d</c> with the smallest <c>d</c> from 1 to 1001 for which
    /// <c>n/d</c>, converted to a float, is equal to <paramref name="fps"/>. A denominator of 1 gives only <c>n</c>.
    /// When no such fraction exists, the result is <c>round(fps × 1,000,000) / 1,000,000</c> in lowest terms.
    /// </summary>
    /// <example>30 gives <c>30</c>, 30.4 gives <c>152/5</c>, and 30000/1001 gives <c>30000/1001</c>.</example>
    /// <param name="fps">The frame rate, more than 0.</param>
    public static string FormatFrameRate(float fps)
    {
        if (!float.IsFinite(fps) || fps <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "The frame rate must be more than 0.");

        for (int denominator = 1; denominator <= MaxRateDenominator; denominator++)
        {
            double numerator = Math.Round((double)fps * denominator);
            if (numerator >= 1.0 && (float)(numerator / denominator) == fps)
            {
                string whole = numerator.ToString("0", CultureInfo.InvariantCulture);
                return denominator == 1 ? whole : string.Create(CultureInfo.InvariantCulture, $"{whole}/{denominator}");
            }
        }

        long scaled = Math.Max(1L, (long)Math.Round((double)fps * FallbackRateDenominator));
        long divisor = GreatestCommonDivisor(scaled, FallbackRateDenominator);
        return string.Create(CultureInfo.InvariantCulture, $"{scaled / divisor}/{FallbackRateDenominator / divisor}");
    }

    /// <summary>
    /// Starts ffmpeg for one output. The session takes frames of <paramref name="width"/> × <paramref name="height"/>
    /// RGBA bytes, top row first. Cancellation of <paramref name="cancellationToken"/> stops the process.
    /// </summary>
    /// <param name="rendition">The output.</param>
    /// <param name="width">The width of the frames, in pixels.</param>
    /// <param name="height">The height of the frames, in pixels.</param>
    /// <param name="fps">The frame rate.</param>
    /// <param name="outputPath">The output file. Its folder must exist.</param>
    /// <param name="cancellationToken">Stops the process and the session.</param>
    /// <exception cref="GaussianBakeException">ffmpeg cannot start. The failure code is <see cref="GaussianBakeFailureCodes.EncodeFailed"/>.</exception>
    public Task<FfmpegSession> StartAsync(LoopRendition rendition, int width, int height, float fps, string outputPath, CancellationToken cancellationToken)
    {
        List<string> arguments = BuildArgumentList(rendition, width, height, fps, H264Encoder, outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        Process process = StartProcess(arguments);
        return Task.FromResult(new FfmpegSession(process, rendition, outputPath, checked(width * height * 4), cancellationToken));
    }

    internal static List<string> BuildArgumentList(LoopRendition rendition, int width, int height, float fps, string h264Encoder, string outputPath)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rendition.Height, nameof(rendition));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string h264Name = H264EncoderNameFor(h264Encoder);

        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgba",
            "-s", string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"),
            "-r", FormatFrameRate(fps),
            "-i", "-",
            "-an", "-threads", "1", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-map_metadata", "-1",
        ];

        if (rendition.Height < height)
            arguments.AddRange(["-vf", string.Create(CultureInfo.InvariantCulture, $"scale=-2:{rendition.Height}:flags=area")]);

        string bitrate = rendition.Height >= 1080 ? "4M" : "1M";
        switch (rendition.Codec)
        {
            case LoopRendition.Vp9Codec:
                arguments.AddRange(["-c:v", Vp9EncoderName, "-b:v", "0", "-crf", "34", "-deadline", "good", "-cpu-used", "2", "-row-mt", "0", "-pix_fmt", "yuv420p"]);
                break;
            case LoopRendition.H264Codec when h264Name == NvencEncoderName:
                arguments.AddRange(["-c:v", NvencEncoderName, "-preset", "p5", "-b:v", bitrate, "-pix_fmt", "yuv420p", "-movflags", "+faststart"]);
                break;
            case LoopRendition.H264Codec:
                arguments.AddRange(["-c:v", OpenH264EncoderName, "-b:v", bitrate, "-pix_fmt", "yuv420p", "-movflags", "+faststart"]);
                break;
            case LoopRendition.WebpCodec:
                arguments.AddRange(["-frames:v", "1", "-c:v", WebpEncoderName, "-quality", "85"]);
                break;
            default:
                throw new ArgumentException($"The codec '{rendition.Codec}' is not a loop codec.", nameof(rendition));
        }

        arguments.Add(outputPath);
        return arguments;
    }

    /// <summary>Returns the last <see cref="FfmpegSession.StderrLineCount"/> lines of a text.</summary>
    internal static string[] LastLines(string text)
    {
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return lines.Length <= FfmpegSession.StderrLineCount ? lines : lines[^FfmpegSession.StderrLineCount..];
    }

    /// <summary>Formats stderr lines for the end of a failure message.</summary>
    internal static string FormatStderr(IReadOnlyList<string> lines)
        => lines.Count == 0
            ? " ffmpeg wrote nothing to stderr."
            : $" The last {lines.Count} stderr lines of ffmpeg:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";

    /// <summary>Stops a process that is still running, and waits a short time for it to end.</summary>
    internal static async Task StopAsync(Process process)
    {
        try
        {
            if (process.HasExited)
                return;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException or NotSupportedException)
        {
            // The process ended already, or the system cannot stop it. Nothing more can be done here.
        }
    }

    private Process StartProcess(IEnumerable<string> arguments)
    {
        ProcessStartInfo info = new(FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        try
        {
            return Process.Start(info)
                ?? throw new GaussianBakeException(GaussianBakeFailureCodes.EncodeFailed, $"ffmpeg '{FfmpegPath}' did not start.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.EncodeFailed,
                $"ffmpeg '{FfmpegPath}' cannot start: {ex.Message} Give its path with --ffmpeg or {XREngineEnvironmentVariables.FfmpegPath}.");
        }
    }

    /// <summary>Returns the ffmpeg encoder name of an H.264 encoder option.</summary>
    private static string H264EncoderNameFor(string h264Encoder)
    {
        if (string.Equals(h264Encoder, BakeSplatPreviewOptions.OpenH264Encoder, StringComparison.OrdinalIgnoreCase))
            return OpenH264EncoderName;
        if (string.Equals(h264Encoder, BakeSplatPreviewOptions.NvencEncoder, StringComparison.OrdinalIgnoreCase))
            return NvencEncoderName;
        throw new ArgumentException(
            $"The H.264 encoder must be {BakeSplatPreviewOptions.OpenH264Encoder} or {BakeSplatPreviewOptions.NvencEncoder}, not '{h264Encoder}'.",
            nameof(h264Encoder));
    }

    /// <summary>Reads the encoder name from one line of an <c>-encoders</c> listing. Returns false for other lines.</summary>
    private static bool TryReadEncoderName(ReadOnlySpan<char> line, out string name)
    {
        name = string.Empty;
        line = line.TrimStart();
        if (line.Length < 8 || line[6] != ' ')
            return false;

        for (int i = 0; i < 6; i++)
        {
            if (!char.IsAsciiLetterUpper(line[i]) && line[i] != '.')
                return false;
        }

        ReadOnlySpan<char> rest = line[7..].TrimStart();
        int end = rest.IndexOfAny(' ', '\t');
        ReadOnlySpan<char> token = end < 0 ? rest : rest[..end];

        // The legend lines at the top, such as " V..... = Video", have '=' in place of a name.
        if (token.IsEmpty || token[0] == '=')
            return false;

        name = token.ToString();
        return true;
    }

    /// <summary>Appends an argument, in quotes when it has white space or a quote (Windows command line rules).</summary>
    private static void AppendQuoted(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes before a quote are doubled, and the quote gets one more backslash.
            line.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            line.Append(c);
            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled.
        line.Append('\\', backslashes * 2);
        line.Append('"');
    }

    private static long GreatestCommonDivisor(long a, long b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }
}
