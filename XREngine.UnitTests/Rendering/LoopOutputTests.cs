using NUnit.Framework;
using Shouldly;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using XREngine.Components.Lights;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Cli;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Video;
using XREngine.Rendering.Models.Gaussian.Baking.Views;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class LoopOutputTests
{
    private const float PoseTolerance = 1e-5f;

    private static readonly Vector3 OrbitTarget = new(1.0f, 2.0f, -3.0f);

    /// <summary>The <c>-encoders</c> output of an ffmpeg build without OpenH264, with near-match names around the real ones.</summary>
    private const string EncodersWithoutOpenH264 =
        """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ..S... = Slice-level multithreading
         ...X.. = Codec is experimental
         ....B. = Supports draw_horiz_band
         .....D = Supports direct rendering method 1
         ------
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D libx264rgb           libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 RGB (codec h264)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         V....D libvpx               libvpx VP8 (codec vp8)
         V....D libvpx-vp9           libvpx VP9 (codec vp9)
         V....D libwebp_anim         libwebp WebP image (codec webp)
         V....D libwebp              libwebp WebP image (codec webp)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    [Test]
    public void Plan_Orbit_IsSeamless()
    {
        GaussianCacheCameraHints hints = OrbitHints();

        BakeCameraPose[] poses = LoopCameraPlanner.Plan(hints, 8.0f, 240);

        poses.Length.ShouldBe(240);
        ShouldMatch(LoopCameraPlanner.PoseAt(hints, 8.0f, 240, 240), poses[0]);

        // The step across the wrap is the same as every other step, so the loop has no jump.
        float step = Vector3.Distance(poses[0].Position, poses[1].Position);
        Vector3.Distance(poses[^1].Position, poses[0].Position).ShouldBe(step, 1e-4f);
        Vector3.Distance(poses[120].Position, poses[121].Position).ShouldBe(step, 1e-4f);

        float pitch = hints.DefaultPitchDegrees * MathF.PI / 180.0f;
        foreach (BakeCameraPose pose in poses)
        {
            pose.VerticalFovDegrees.ShouldBe(40.0f);
            Vector3.Distance(pose.Position, OrbitTarget).ShouldBe(hints.DefaultRadius, 1e-4f);
            (pose.Position.Y - OrbitTarget.Y).ShouldBe(hints.DefaultRadius * MathF.Sin(pitch), 1e-4f);
            Vector3.Dot(Forward(pose), Vector3.Normalize(OrbitTarget - pose.Position)).ShouldBeGreaterThan(0.99999f);
            Up(pose).Y.ShouldBeGreaterThan(0.0f);
        }

        // Frame 0 is at the default yaw: yaw 0 puts the camera on +Z of the target, and the yaw turns toward +X.
        float yaw = hints.DefaultYawDegrees * MathF.PI / 180.0f;
        Vector3 expected = OrbitTarget + hints.DefaultRadius * new Vector3(
            MathF.Sin(yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            MathF.Cos(yaw) * MathF.Cos(pitch));
        Vector3.Distance(poses[0].Position, expected).ShouldBeLessThan(1e-4f);

        // A quarter of the loop turns the camera 90 degrees around the target.
        Vector3 start = Flat(poses[0].Position - OrbitTarget);
        Vector3 quarter = Flat(poses[60].Position - OrbitTarget);
        Vector3.Dot(start, quarter).ShouldBe(0.0f, 1e-4f);
    }

    [Test]
    public void Plan_Path_InterpolatesKeys()
    {
        Vector3 a = new(0.0f, 1.0f, 5.0f);
        Vector3 b = new(4.0f, 1.0f, 5.0f);
        Quaternion rotationA = Quaternion.Identity;
        Quaternion rotationB = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2.0f);
        GaussianCacheCameraHints hints = OrbitHints();
        hints.Path = [new GaussianCacheCameraKey(0.0f, a, rotationA), new GaussianCacheCameraKey(2.0f, b, rotationB)];

        // 8 frames over 4 seconds: frame i is at 0.5 i seconds.
        BakeCameraPose[] poses = LoopCameraPlanner.Plan(hints, 4.0f, 8);

        poses.Length.ShouldBe(8);
        poses.ShouldAllBe(pose => pose.VerticalFovDegrees == 40.0f);
        ShouldMatch(poses[0], new BakeCameraPose(a, rotationA, 40.0f));
        ShouldMatch(poses[1], new BakeCameraPose(Vector3.Lerp(a, b, 0.25f), YawDegrees(22.5f), 40.0f));
        ShouldMatch(poses[4], new BakeCameraPose(b, rotationB, 40.0f));

        // After the last key, the path goes back to the first key at the loop duration.
        ShouldMatch(poses[6], new BakeCameraPose(Vector3.Lerp(b, a, 0.5f), YawDegrees(45.0f), 40.0f));
        ShouldMatch(poses[7], new BakeCameraPose(Vector3.Lerp(b, a, 0.75f), YawDegrees(22.5f), 40.0f));
        ShouldMatch(LoopCameraPlanner.PoseAt(hints, 4.0f, 8, 8), poses[0]);
    }

    [Test]
    public void Plan_PathWithFirstKeyAfterZero_WrapsFromLastKey()
    {
        Vector3 a = new(2.0f, 0.0f, 0.0f);
        Vector3 b = new(6.0f, 0.0f, 0.0f);
        GaussianCacheCameraHints hints = OrbitHints();

        // The keys are not in time order. The planner sorts them.
        hints.Path = [new GaussianCacheCameraKey(2.0f, b, Quaternion.Identity), new GaussianCacheCameraKey(1.0f, a, Quaternion.Identity)];

        // 4 frames over 4 seconds. The segment after key b (t = 2) ends at key a one loop later (t = 1 + 4).
        BakeCameraPose[] poses = LoopCameraPlanner.Plan(hints, 4.0f, 4);

        poses[0].Position.X.ShouldBe(6.0f - (4.0f * 2.0f / 3.0f), PoseTolerance);
        poses[1].Position.X.ShouldBe(2.0f, PoseTolerance);
        poses[2].Position.X.ShouldBe(6.0f, PoseTolerance);
        poses[3].Position.X.ShouldBe(6.0f - (4.0f / 3.0f), PoseTolerance);
    }

    [TestCase(GaussianBakeKind.Avatar, 8.0f, 240)]
    [TestCase(GaussianBakeKind.Item, 8.0f, 240)]
    [TestCase(GaussianBakeKind.World, 10.0f, 300)]
    public void ChooseTiming_StillCache_UsesKindLength(GaussianBakeKind kind, float seconds, int frames)
    {
        GaussianCacheHeader header = new() { FrameCount = 1, FrameRate = 30.0f, DurationSeconds = 0.0f, LoopMode = GaussianCacheLoopMode.Once };

        LoopTiming timing = LoopCameraPlanner.ChooseTiming(kind, header);

        timing.ShouldBe(new LoopTiming(seconds, 30.0f, frames));
    }

    [Test]
    public void ChooseTiming_AnimatedLoop_RepeatsClipForMinimumTurntable()
    {
        // A clip of 5 s plays two times, so the turntable takes 10 s.
        GaussianCacheHeader header = new() { FrameCount = 152, FrameRate = 30.4f, DurationSeconds = 5.0f, LoopMode = GaussianCacheLoopMode.Loop };

        LoopTiming timing = LoopCameraPlanner.ChooseTiming(GaussianBakeKind.Avatar, header);

        timing.ShouldBe(new LoopTiming(10.0f, 30.4f, 304));
    }

    [Test]
    public void ChooseTiming_AnimatedLoopLongerThanMinimum_PlaysClipOneTime()
    {
        GaussianCacheHeader header = new() { FrameCount = 200, FrameRate = 30.0f, DurationSeconds = 200.0f / 30.0f, LoopMode = GaussianCacheLoopMode.Loop };

        LoopTiming timing = LoopCameraPlanner.ChooseTiming(GaussianBakeKind.Item, header);

        timing.FrameCount.ShouldBe(200);
        timing.DurationSeconds.ShouldBe(200.0f / 30.0f, 1e-6f);
    }

    [Test]
    public void ChooseTiming_AnimatedPingPongAvatar_RepeatsRoundTripForMinimumTurntable()
    {
        // One round trip is 2 x (32 - 1) = 62 frames, about 2.07 s. Three round trips give 186 frames, 6.2 s.
        GaussianCacheHeader header = new() { FrameCount = 32, FrameRate = 30.0f, DurationSeconds = 32.0f / 30.0f, LoopMode = GaussianCacheLoopMode.PingPong };

        LoopTiming timing = LoopCameraPlanner.ChooseTiming(GaussianBakeKind.Avatar, header);

        timing.FrameCount.ShouldBe(186);
        timing.FramesPerSecond.ShouldBe(30.0f);
        timing.DurationSeconds.ShouldBe(186.0f / 30.0f, 1e-6f);
        (timing.FrameCount % 62).ShouldBe(0);
        timing.DurationSeconds.ShouldBeGreaterThanOrEqualTo(LoopCameraPlanner.MinTurntableSeconds);
    }

    [Test]
    public void ChooseTiming_RepeatedClip_IsSeamlessInAnimationAndCamera()
    {
        GaussianCacheHeader header = new() { FrameCount = 64, FrameRate = 30.0f, DurationSeconds = 64.0f / 30.0f, LoopMode = GaussianCacheLoopMode.Loop };
        LoopTiming timing = LoopCameraPlanner.ChooseTiming(GaussianBakeKind.Avatar, header);
        timing.FrameCount.ShouldBe(192);

        // The frame after the last is frame 0 of the clip and frame 0 of the camera, and the camera turns one time.
        float wrapFrame = GaussianCachePoseReference.FractionalFrame(header, timing.FrameCount / timing.FramesPerSecond);
        MathF.Min(wrapFrame, header.FrameCount - wrapFrame).ShouldBeLessThan(1e-3f);
        GaussianCacheCameraHints hints = OrbitHints();
        BakeCameraPose[] poses = LoopCameraPlanner.Plan(hints, timing.DurationSeconds, timing.FrameCount);
        ShouldMatch(LoopCameraPlanner.PoseAt(hints, timing.DurationSeconds, timing.FrameCount, timing.FrameCount), poses[0]);
        Vector3 start = Flat(poses[0].Position - OrbitTarget);
        Vector3.Dot(start, Flat(poses[timing.FrameCount / 4].Position - OrbitTarget)).ShouldBe(0.0f, 1e-4f);
        Vector3.Dot(start, Flat(poses[timing.FrameCount / 2].Position - OrbitTarget)).ShouldBeLessThan(0.0f);
    }

    [Test]
    public void ChooseTiming_PingPong_PlaysForwardAndBack()
    {
        GaussianCacheHeader header = new() { FrameCount = 64, FrameRate = 30.0f, DurationSeconds = 64.0f / 30.0f, LoopMode = GaussianCacheLoopMode.PingPong };

        LoopTiming timing = LoopCameraPlanner.ChooseTiming(GaussianBakeKind.World, header);

        // Frames 0..63 forward, then 62..1 back; frame 126 is frame 0 again.
        timing.FrameCount.ShouldBe(126);
        timing.FramesPerSecond.ShouldBe(30.0f);
        timing.DurationSeconds.ShouldBe(126.0f / 30.0f, 1e-6f);
    }

    [Test]
    public void Renditions_ListTheSixOutputs()
    {
        LoopRendition.All.ShouldBe(
        [
            new LoopRendition(1080, "vp9", "loop-1080.webm"),
            new LoopRendition(1080, "h264", "loop-1080.mp4"),
            new LoopRendition(480, "vp9", "loop-480.webm"),
            new LoopRendition(480, "h264", "loop-480.mp4"),
            new LoopRendition(1080, "webp", "poster-1080.webp"),
            new LoopRendition(480, "webp", "poster-480.webp"),
        ]);
    }

    [Test]
    public void BuildArguments_Vp9_1080_IsPinned()
    {
        string arguments = FfmpegEncoder.BuildArguments(
            LoopRendition.Loop1080Vp9, 1920, 1080, 30.0f, BakeSplatPreviewOptions.OpenH264Encoder, "out/loop-1080.webm");

        arguments.ShouldBe(
            "-hide_banner -loglevel error -y -f rawvideo -pix_fmt rgba -s 1920x1080 -r 30 -i - -an -threads 1 " +
            "-fflags +bitexact -flags:v +bitexact -map_metadata -1 -vf scale=out_color_matrix=bt709:out_range=tv " +
            "-c:v libvpx-vp9 -b:v 0 -crf 34 -deadline good -cpu-used 2 -row-mt 0 -pix_fmt yuv420p " +
            "-colorspace bt709 -color_primaries bt709 -color_trc bt709 out/loop-1080.webm");
    }

    [Test]
    public void BuildArguments_Openh264_480_IsPinned()
    {
        string arguments = FfmpegEncoder.BuildArguments(
            LoopRendition.Loop480H264, 1920, 1080, 30.0f, BakeSplatPreviewOptions.OpenH264Encoder, "out/loop-480.mp4");

        arguments.ShouldBe(
            "-hide_banner -loglevel error -y -f rawvideo -pix_fmt rgba -s 1920x1080 -r 30 -i - -an -threads 1 " +
            "-fflags +bitexact -flags:v +bitexact -map_metadata -1 -vf scale=-2:480:flags=area:out_color_matrix=bt709:out_range=tv " +
            "-c:v libopenh264 -b:v 1M -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 " +
            "-movflags +faststart out/loop-480.mp4");
    }

    [Test]
    public void BuildArguments_OtherRenditions_ArePinned()
    {
        const string common =
            "-hide_banner -loglevel error -y -f rawvideo -pix_fmt rgba -s 1920x1080 -r 30 -i - -an -threads 1 " +
            "-fflags +bitexact -flags:v +bitexact -map_metadata -1 ";

        const string tags = "-colorspace bt709 -color_primaries bt709 -color_trc bt709 ";
        const string scale1080 = "-vf scale=out_color_matrix=bt709:out_range=tv ";
        const string scale480 = "-vf scale=-2:480:flags=area:out_color_matrix=bt709:out_range=tv ";
        FfmpegEncoder.BuildArguments(LoopRendition.Loop1080H264, 1920, 1080, 30.0f, BakeSplatPreviewOptions.OpenH264Encoder, "a.mp4")
            .ShouldBe(common + scale1080 + "-c:v libopenh264 -b:v 4M -pix_fmt yuv420p " + tags + "-movflags +faststart a.mp4");
        FfmpegEncoder.BuildArguments(LoopRendition.Loop1080H264, 1920, 1080, 30.0f, BakeSplatPreviewOptions.NvencEncoder, "a.mp4")
            .ShouldBe(common + scale1080 + "-c:v h264_nvenc -preset p5 -b:v 4M -pix_fmt yuv420p " + tags + "-movflags +faststart a.mp4");
        FfmpegEncoder.BuildArguments(LoopRendition.Loop480H264, 1920, 1080, 30.0f, BakeSplatPreviewOptions.NvencEncoder, "a.mp4")
            .ShouldBe(common + scale480 + "-c:v h264_nvenc -preset p5 -b:v 1M -pix_fmt yuv420p " + tags + "-movflags +faststart a.mp4");
        FfmpegEncoder.BuildArguments(LoopRendition.Loop480Vp9, 1920, 1080, 30.0f, BakeSplatPreviewOptions.OpenH264Encoder, "a.webm")
            .ShouldBe(common + scale480 + "-c:v libvpx-vp9 -b:v 0 -crf 34 -deadline good -cpu-used 2 -row-mt 0 -pix_fmt yuv420p " + tags + "a.webm");
        FfmpegEncoder.BuildArguments(LoopRendition.Poster1080, 1920, 1080, 30.0f, BakeSplatPreviewOptions.OpenH264Encoder, "p.webp")
            .ShouldBe(common + "-frames:v 1 -c:v libwebp -quality 85 p.webp");
        FfmpegEncoder.BuildArguments(LoopRendition.Poster480, 1920, 1080, 30.0f, BakeSplatPreviewOptions.OpenH264Encoder, "p.webp")
            .ShouldBe(common + "-vf scale=-2:480:flags=area -frames:v 1 -c:v libwebp -quality 85 p.webp");
    }

    [Test]
    public void BuildArguments_NeverSelectsLibx264()
    {
        foreach (string h264Encoder in new[] { BakeSplatPreviewOptions.OpenH264Encoder, BakeSplatPreviewOptions.NvencEncoder })
            foreach (LoopRendition rendition in LoopRendition.All)
                FfmpegEncoder.BuildArguments(rendition, 1920, 1080, 30.0f, h264Encoder, "out.bin").ShouldNotContain("x264");
    }

    [Test]
    public void BuildArguments_PathWithSpace_IsQuoted()
    {
        string arguments = FfmpegEncoder.BuildArguments(
            LoopRendition.Poster1080, 1920, 1080, 30.0f, BakeSplatPreviewOptions.OpenH264Encoder, @"C:\Bake Out\poster-1080.webp");

        arguments.ShouldEndWith(@" -quality 85 ""C:\Bake Out\poster-1080.webp""");
    }

    [TestCase(30.0f, "30")]
    [TestCase(24.0f, "24")]
    [TestCase(60.0f, "60")]
    [TestCase(30.4f, "152/5")]
    [TestCase(30000.0f / 1001.0f, "30000/1001")]
    [TestCase(24000.0f / 1001.0f, "24000/1001")]
    [TestCase(152.0f / 5.0f, "152/5")]
    [TestCase(12.5f, "25/2")]
    public void FormatFrameRate_GivesExactRational(float fps, string expected)
    {
        FfmpegEncoder.FormatFrameRate(fps).ShouldBe(expected);
        FfmpegEncoder.BuildArguments(LoopRendition.Loop1080Vp9, 1920, 1080, fps, BakeSplatPreviewOptions.OpenH264Encoder, "a.webm")
            .ShouldContain($" -r {expected} -i - ");
    }

    [Test]
    public void FormatFrameRate_IrregularRate_StaysWithinFloatPrecision()
    {
        const float fps = 29.123457f;

        string text = FfmpegEncoder.FormatFrameRate(fps);

        string[] parts = text.Split('/');
        double value = double.Parse(parts[0], CultureInfo.InvariantCulture)
            / (parts.Length == 2 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 1.0);
        Math.Abs(value - fps).ShouldBeLessThanOrEqualTo(fps * 2e-7);
    }

    [Test]
    public void VerifyEncoders_ReportsMissing()
    {
        FfmpegEncoder.FindMissingEncoders(EncodersWithoutOpenH264, BakeSplatPreviewOptions.OpenH264Encoder)
            .ShouldBe(["libopenh264"]);
        FfmpegEncoder.FindMissingEncoders(EncodersWithoutOpenH264, BakeSplatPreviewOptions.NvencEncoder)
            .ShouldBeEmpty();
    }

    [Test]
    public void FindMissingEncoders_MatchesNamesExactly()
    {
        const string nearMatches =
            """
             ------
             V....D libvpx               libvpx VP8 (codec vp8)
             V....D libvpx-vp9x          not an encoder name that the bake uses
             V....D libwebp_anim         libwebp WebP image (codec webp)
             V....D libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             V....D h264_nvenc_extra     not an encoder name that the bake uses
             libwebp is named here only in a description line
            """;

        FfmpegEncoder.FindMissingEncoders(nearMatches, BakeSplatPreviewOptions.OpenH264Encoder)
            .ShouldBe(["libvpx-vp9", "libwebp"]);
        FfmpegEncoder.FindMissingEncoders(nearMatches, BakeSplatPreviewOptions.NvencEncoder)
            .ShouldBe(["libvpx-vp9", "h264_nvenc", "libwebp"]);
        FfmpegEncoder.FindMissingEncoders(string.Empty, BakeSplatPreviewOptions.OpenH264Encoder)
            .ShouldBe(["libvpx-vp9", "libopenh264", "libwebp"]);
    }

    [Test]
    public void PreviewCameraPath_CookedPrefab_KeepsKeys()
    {
        new PreviewCameraPathComponent().Keys.ShouldBeEmpty();

        GaussianCacheCameraKey[] keys =
        [
            new GaussianCacheCameraKey(0.0f, new Vector3(1.0f, 2.0f, 3.0f), YawDegrees(10.0f)),
            new GaussianCacheCameraKey(2.5f, new Vector3(-4.0f, 0.5f, 6.0f), YawDegrees(-80.0f)),
        ];
        SceneNode root = new("PathRoot");
        PreviewCameraPathComponent path = root.AddComponent<PreviewCameraPathComponent>().ShouldNotBeNull();
        path.Keys = keys;
        string folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "LoopOutputTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string pak = Path.Combine(folder, "path.pak");
            GaussianBakeTestAssets.WritePak(pak, ("Fixtures/Path.asset", GaussianBakeTestAssets.Cook(new XRPrefabSource { RootNode = root })));

            CookedBakeInput input = CookedBakeInputLoader.Load(pak, "Fixtures/Path.asset", GaussianBakeKind.Item);

            SceneNode loaded = input.PrefabRoot.ShouldNotBeNull();
            loaded.GetComponent<PreviewCameraPathComponent>().ShouldNotBeNull().Keys.ShouldBe(keys);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test, Category("Hardware")]
    public async Task Encode_SameFramesTwice_IsByteIdentical()
    {
        const int width = 320;
        const int height = 180;
        const int frameCount = 16;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(120));
        FfmpegEncoder encoder = await RequireEncoderAsync(timeout.Token);

        byte[][] frames = new byte[frameCount][];
        for (int i = 0; i < frameCount; i++)
            frames[i] = SyntheticFrame(width, height, i);

        string root = Path.Combine(Path.GetTempPath(), "xre-loop-output-tests", Guid.NewGuid().ToString("N"));
        try
        {
            LoopRendition[] renditions = [LoopRendition.Loop1080Vp9, LoopRendition.Loop1080H264];
            string first = Path.Combine(root, "first");
            string second = Path.Combine(root, "second");
            foreach (LoopRendition rendition in renditions)
            {
                await EncodeAsync(encoder, rendition, frames, width, height, Path.Combine(first, rendition.FileName), timeout.Token);
                await EncodeAsync(encoder, rendition, frames, width, height, Path.Combine(second, rendition.FileName), timeout.Token);

                byte[] a = await File.ReadAllBytesAsync(Path.Combine(first, rendition.FileName), timeout.Token);
                byte[] b = await File.ReadAllBytesAsync(Path.Combine(second, rendition.FileName), timeout.Token);
                a.Length.ShouldBeGreaterThan(0);
                TestContext.Out.WriteLine($"{rendition.FileName}: {a.Length} bytes, SHA-256 {Convert.ToHexString(SHA256.HashData(a))}");
                SHA256.HashData(b).ShouldBe(SHA256.HashData(a), $"{rendition.FileName} is not byte-identical");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test, Category("Hardware")]
    public async Task Session_OutputFolderMissing_ThrowsEncodeFailedWithStderr()
    {
        const int width = 64;
        const int height = 64;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        FfmpegEncoder encoder = await RequireEncoderAsync(timeout.Token);
        string output = Path.Combine(Path.GetTempPath(), "xre-loop-output-tests", Guid.NewGuid().ToString("N"), "missing", "loop-1080.webm");
        byte[] frame = SyntheticFrame(width, height, 0);

        GaussianBakeException failure = await Should.ThrowAsync<GaussianBakeException>(async () =>
        {
            await using FfmpegSession session = await encoder.StartAsync(LoopRendition.Loop1080Vp9, width, height, 30.0f, output, timeout.Token);
            for (int i = 0; i < 8; i++)
                await session.WriteFrameAsync(frame);
            await session.CompleteAsync();
        });

        failure.FailureCode.ShouldBe(GaussianBakeFailureCodes.EncodeFailed);
        failure.Message.ShouldContain("loop-1080.webm");
        // The message ends with the stderr lines of ffmpeg, which name the output that it cannot open.
        failure.Message.Split('\n').Length.ShouldBeGreaterThan(1);
        TestContext.Out.WriteLine(failure.Message);
    }

    private static async Task<FfmpegEncoder> RequireEncoderAsync(CancellationToken cancellationToken)
    {
        string? configured = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.FfmpegPath);
        string ffmpeg = string.IsNullOrWhiteSpace(configured) ? BakeSplatPreviewOptions.DefaultFfmpegCommand : configured;
        FfmpegEncoder encoder = new(ffmpeg, BakeSplatPreviewOptions.OpenH264Encoder);
        IReadOnlyList<string> missing;
        try
        {
            missing = await encoder.VerifyEncodersAsync(cancellationToken);
        }
        catch (GaussianBakeException ex)
        {
            Assert.Inconclusive($"ffmpeg is not available: {ex.Message}");
            throw;
        }

        if (missing.Count > 0)
            Assert.Inconclusive($"ffmpeg has no {string.Join(", ", missing)} encoder.");
        return encoder;
    }

    private static async Task EncodeAsync(
        FfmpegEncoder encoder,
        LoopRendition rendition,
        byte[][] frames,
        int width,
        int height,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using FfmpegSession session = await encoder.StartAsync(rendition, width, height, 30.0f, outputPath, cancellationToken);
        foreach (byte[] frame in frames)
            await session.WriteFrameAsync(frame);
        await session.CompleteAsync();
    }

    /// <summary>Makes an opaque RGBA frame with moving gradients and a moving square.</summary>
    private static byte[] SyntheticFrame(int width, int height, int index)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                bool square = Math.Abs(x - (40 + (index * 12))) < 20 && Math.Abs(y - (height / 2)) < 20;
                rgba[offset] = (byte)(square ? 250 : (x + (index * 4)) & 0xFF);
                rgba[offset + 1] = (byte)(square ? 30 : ((y * 2) + (index * 3)) & 0xFF);
                rgba[offset + 2] = (byte)(square ? 40 : ((x ^ y) + (index * 7)) & 0xFF);
                rgba[offset + 3] = 255;
            }
        }

        return rgba;
    }

    private static GaussianCacheCameraHints OrbitHints()
        => new()
        {
            OrbitTarget = OrbitTarget,
            MinRadius = 2.5f,
            MaxRadius = 15.0f,
            DefaultRadius = 5.0f,
            DefaultYawDegrees = 30.0f,
            DefaultPitchDegrees = 15.0f,
        };

    private static Quaternion YawDegrees(float degrees)
        => Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180.0f);

    private static Vector3 Flat(Vector3 value)
        => Vector3.Normalize(new Vector3(value.X, 0.0f, value.Z));

    private static Vector3 Forward(BakeCameraPose pose)
        => Vector3.Transform(-Vector3.UnitZ, pose.Rotation);

    private static Vector3 Up(BakeCameraPose pose)
        => Vector3.Transform(Vector3.UnitY, pose.Rotation);

    /// <summary>Compares two poses. The rotations are compared by their axes, so q and −q are equal.</summary>
    private static void ShouldMatch(BakeCameraPose actual, BakeCameraPose expected)
    {
        Vector3.Distance(actual.Position, expected.Position).ShouldBeLessThanOrEqualTo(PoseTolerance);
        Vector3.Distance(Forward(actual), Forward(expected)).ShouldBeLessThanOrEqualTo(PoseTolerance);
        Vector3.Distance(Up(actual), Up(expected)).ShouldBeLessThanOrEqualTo(PoseTolerance);
        actual.VerticalFovDegrees.ShouldBe(expected.VerticalFovDegrees);
    }
}
