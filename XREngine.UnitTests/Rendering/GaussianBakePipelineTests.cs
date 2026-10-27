using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Build;
using XREngine.Rendering.Models.Gaussian.Baking.Cli;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Output;
using XREngine.Rendering.Models.Gaussian.Baking.Pipeline;
using XREngine.Rendering.Models.Gaussian.Baking.Report;
using XREngine.Rendering.Models.Gaussian.Baking.Rendering;
using XREngine.Rendering.Models.Gaussian.Baking.Video;
using XREngine.Rendering.Models.Gaussian.Baking.Views;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Runs the gaussian bake pipeline with a fake renderer. The fake gives a constant mid-gray image for each view and
/// writes empty loop files, so the tests need no GPU and no ffmpeg.
/// </summary>
[TestFixture]
public sealed class GaussianBakePipelineTests
{
    private const string AvatarAssetPath = "Fixtures/Avatar.asset";
    private const string EmptyAssetPath = "Fixtures/Empty.asset";

    /// <summary>A small splat count keeps the avatar bakes fast.</summary>
    private const int TestSplatCount = 4_000;

    private string _workRoot = string.Empty;
    private string _avatarPak = string.Empty;
    private string _emptyPak = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _workRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "GaussianBakePipeline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workRoot);

        _avatarPak = Path.Combine(_workRoot, "avatar.pak");
        XRPrefabSource avatar = GaussianBakeTestAssets.ImportPrefab(GaussianBakeTestAssets.SkinnedAvatarModelPath);
        GaussianBakeTestAssets.WritePak(_avatarPak, (AvatarAssetPath, GaussianBakeTestAssets.Cook(avatar)));

        _emptyPak = Path.Combine(_workRoot, "empty.pak");
        XRPrefabSource empty = new() { Name = "Empty", RootNode = new SceneNode("EmptyRoot") };
        GaussianBakeTestAssets.WritePak(_emptyPak, (EmptyAssetPath, GaussianBakeTestAssets.Cook(empty)));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (Directory.Exists(_workRoot))
            Directory.Delete(_workRoot, recursive: true);
    }

    [Test]
    public void Report_SerializesSchemaV1PropertyNames()
    {
        BakeReport report = CreateFullReport();

        using JsonDocument document = JsonDocument.Parse(BakeReportWriter.Serialize(report));
        JsonElement root = document.RootElement;

        PropertyNames(root).ShouldBe(
        [
            "schemaVersion", "status", "failureCode", "failureDetail", "engineVersion", "meshoptimizerVersion",
            "cacheFormatVersion", "representation", "kind", "input", "clip", "splats", "driver", "frames", "gridBits",
            "colorScale", "cache", "outputs", "timingsMs", "skipped", "warnings",
        ]);
        PropertyNames(root.GetProperty("input")).ShouldBe(["pak", "asset", "sha256"]);
        PropertyNames(root.GetProperty("clip")).ShouldBe(["name", "source", "durationSeconds", "loopMode"]);
        PropertyNames(root.GetProperty("splats")).ShouldBe(["total", "static", "perLevel"]);
        PropertyNames(root.GetProperty("driver")).ShouldBe(["verticesPerLevel", "trianglesPerLevel"]);
        PropertyNames(root.GetProperty("frames")).ShouldBe(["count", "rate"]);
        PropertyNames(root.GetProperty("cache")).ShouldBe(["contentHash", "chunk0Bytes", "totalBytes", "chunks"]);
        PropertyNames(root.GetProperty("cache").GetProperty("chunks")[0])
            .ShouldBe(["index", "kind", "splatLevel", "driverLevel", "temporalLevel", "bytes", "sha256"]);
        PropertyNames(root.GetProperty("outputs")[0]).ShouldBe(["file", "height", "codec", "bytes"]);
        PropertyNames(root.GetProperty("timingsMs"))
            .ShouldBe(["load", "sample", "color", "animate", "tree", "driver", "encode", "loop"]);
        PropertyNames(root.GetProperty("skipped")[0]).ShouldBe(["node", "component", "reason"]);

        root.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        root.GetProperty("status").GetString().ShouldBe("ready");
        root.GetProperty("failureCode").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("failureDetail").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("meshoptimizerVersion").GetString().ShouldBe("v1.3 9e1f07b159d3cb777f1c67ed31fc11fd117986f4");
        root.GetProperty("cacheFormatVersion").GetInt32().ShouldBe(1);
        root.GetProperty("representation").GetString().ShouldBe("BakedDriver");
        root.GetProperty("kind").GetString().ShouldBe("avatar");
        root.GetProperty("clip").GetProperty("loopMode").GetString().ShouldBe("Loop");
        root.GetProperty("clip").GetProperty("durationSeconds").GetSingle().ShouldBe(4.0f);
        root.GetProperty("colorScale").GetSingle().ShouldBe(1.7f);
        root.GetProperty("cache").GetProperty("chunks")[0].GetProperty("kind").GetString().ShouldBe("Root");
        root.GetProperty("warnings")[0].GetString().ShouldBe("clip-truncated");
    }

    [Test]
    public void OutputDirectory_Commit_RemovesStaleFiles()
    {
        string final = Path.Combine(_workRoot, "commit");
        Directory.CreateDirectory(final);
        File.WriteAllText(Path.Combine(final, "old.txt"), "stale");

        BakeOutputDirectory output = BakeOutputDirectory.Begin(final);
        output.StagingPath.ShouldStartWith(Path.GetFullPath(final) + ".staging-");
        Directory.Exists(output.StagingPath).ShouldBeTrue();
        File.WriteAllText(Path.Combine(output.StagingPath, "new.txt"), "fresh");
        output.Commit();

        File.Exists(Path.Combine(final, "old.txt")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(final, "new.txt")).ShouldBe("fresh");
        Directory.Exists(output.StagingPath).ShouldBeFalse();
    }

    [Test]
    public void OutputDirectory_Fail_LeavesOnlyReport()
    {
        string final = Path.Combine(_workRoot, "fail");
        Directory.CreateDirectory(final);
        File.WriteAllText(Path.Combine(final, "old.txt"), "stale");
        BakeOutputDirectory output = BakeOutputDirectory.Begin(final);
        File.WriteAllText(Path.Combine(output.StagingPath, "partial.bin"), "partial");
        BakeReport report = CreateFullReport();
        report.MarkFailed(GaussianBakeFailureCodes.BudgetExceeded, "chunk0: 300000 > 262144");

        output.Fail(report);

        Directory.GetFileSystemEntries(final).Select(Path.GetFileName).ShouldBe([BakeReportWriter.FileName]);
        Directory.Exists(output.StagingPath).ShouldBeFalse();
        JsonElement written = ReadReport(final);
        written.GetProperty("status").GetString().ShouldBe("failed");
        written.GetProperty("failureCode").GetString().ShouldBe("budget-exceeded");
        written.GetProperty("failureDetail").GetString().ShouldBe("chunk0: 300000 > 262144");
    }

    [Test]
    public async Task Pipeline_EmptyPrefab_FailsContentEmpty()
    {
        FakeRenderServices render = new();
        string output = Path.Combine(_workRoot, "empty-out");
        GaussianBakePipeline pipeline = CreatePipeline(render);

        BakeReport report = await pipeline.RunAsync(Options(_emptyPak, EmptyAssetPath, "item", output), CancellationToken.None);

        report.Status.ShouldBe(BakeReport.FailedStatus);
        report.FailureCode.ShouldBe(GaussianBakeFailureCodes.ContentEmpty);
        report.FailureDetail.ShouldNotBeNullOrWhiteSpace();
        render.CaptureCalls.ShouldBe(0);
        render.LoopCalls.ShouldBe(0);
        Directory.GetFileSystemEntries(output).Select(Path.GetFileName).ShouldBe([BakeReportWriter.FileName]);
        JsonElement written = ReadReport(output);
        written.GetProperty("status").GetString().ShouldBe("failed");
        written.GetProperty("failureCode").GetString().ShouldBe("content-empty");
    }

    [Test]
    public async Task Pipeline_MissingEncoder_FailsBeforeCapture()
    {
        FakeRenderServices render = new();
        int verifyCalls = 0;
        GaussianBakePipeline pipeline = new(
            render,
            GaussianCacheTestSphere.Meshopt,
            (_, _) =>
            {
                verifyCalls++;
                return Task.FromResult<IReadOnlyList<string>>([FfmpegEncoder.OpenH264EncoderName]);
            });
        string output = Path.Combine(_workRoot, "missing-encoder-out");

        // The archive does not exist: the encoder check fails first, so the load never runs.
        string missingPak = Path.Combine(_workRoot, "does-not-exist.pak");
        BakeReport report = await pipeline.RunAsync(Options(missingPak, AvatarAssetPath, "avatar", output), CancellationToken.None);

        report.Status.ShouldBe(BakeReport.FailedStatus);
        report.FailureCode.ShouldBe(GaussianBakeFailureCodes.EncodeFailed);
        report.FailureDetail.ShouldNotBeNull().ShouldContain(FfmpegEncoder.OpenH264EncoderName);
        verifyCalls.ShouldBe(1);
        render.CaptureCalls.ShouldBe(0);
        render.ContentCalls.ShouldBe(0);
        render.LoopCalls.ShouldBe(0);
        ReadReport(output).GetProperty("failureCode").GetString().ShouldBe("encode-failed");
    }

    [Test]
    public async Task Pipeline_OptimizedFit_FailsUnsupportedOption()
    {
        FakeRenderServices render = new();
        string output = Path.Combine(_workRoot, "optimized-fit-out");
        GaussianBakePipeline pipeline = CreatePipeline(render);

        BakeReport report = await pipeline.RunAsync(
            Options(_avatarPak, AvatarAssetPath, "avatar", output, "--color-mode", "optimized-fit"),
            CancellationToken.None);

        report.Status.ShouldBe(BakeReport.FailedStatus);
        report.FailureCode.ShouldBe(GaussianBakeFailureCodes.UnsupportedOption);
        report.FailureDetail.ShouldNotBeNull().ShouldContain("optimized-fit");
        render.CaptureCalls.ShouldBe(0);
        render.ContentCalls.ShouldBe(0);
        ReadReport(output).GetProperty("failureCode").GetString().ShouldBe("unsupported-option");
    }

    [Test]
    public async Task Pipeline_GlbAvatar_IsDeterministic()
    {
        string first = Path.Combine(_workRoot, "avatar-a");
        string second = Path.Combine(_workRoot, "avatar-b");

        BakeReport a = await CreatePipeline(new FakeRenderServices()).RunAsync(AvatarOptions(first), CancellationToken.None);
        BakeReport b = await CreatePipeline(new FakeRenderServices()).RunAsync(AvatarOptions(second), CancellationToken.None);

        a.Status.ShouldBe(BakeReport.ReadyStatus, a.FailureDetail);
        b.Status.ShouldBe(BakeReport.ReadyStatus, b.FailureDetail);
        a.Cache.ShouldNotBeNull().ContentHash.ShouldBe(b.Cache.ShouldNotBeNull().ContentHash);

        string[] firstFiles = RelativeFiles(first);
        firstFiles.ShouldBe(RelativeFiles(second));
        firstFiles.ShouldContain(Path.Combine(BakeCacheFiles.FolderName, BakeCacheFiles.RootFileName));
        foreach (string file in firstFiles.Where(file => file != BakeReportWriter.FileName))
            Sha256Of(Path.Combine(first, file)).ShouldBe(Sha256Of(Path.Combine(second, file)), file);
    }

    [Test]
    public async Task Pipeline_GlbAvatar_ReportMatchesCache()
    {
        FakeRenderServices render = new();
        string output = Path.Combine(_workRoot, "avatar-report");

        BakeReport report = await CreatePipeline(render).RunAsync(AvatarOptions(output), CancellationToken.None);

        report.Status.ShouldBe(BakeReport.ReadyStatus, report.FailureDetail);
        report.FailureCode.ShouldBeNull();
        render.CaptureCalls.ShouldBeGreaterThan(0);
        render.LoopCalls.ShouldBe(1);

        GaussianCache cache = ReadAsset<GaussianCache>(Path.Combine(output, BakeCacheFiles.FolderName, BakeCacheFiles.RootFileName));
        GaussianCacheHeader header = cache.Header;
        foreach (string chunkPath in cache.ChunkAssetPaths)
            File.Exists(Path.Combine(output, BakeCacheFiles.FolderName, chunkPath)).ShouldBeTrue(chunkPath);

        BakeReportSplats splats = report.Splats.ShouldNotBeNull();
        splats.PerLevel.ShouldBe(header.SplatLevelCounts);
        splats.Total.ShouldBe(TestSplatCount);
        splats.Total.ShouldBe(header.SplatLevelCounts[^1]);
        splats.Static.ShouldBe(header.StaticSplatCounts[^1]);
        BakeReportDriver driver = report.Driver.ShouldNotBeNull();
        driver.VerticesPerLevel.ShouldBe(header.DriverVertexCounts);
        driver.TrianglesPerLevel.ShouldBe(header.DriverTriangleCounts);
        BakeReportFrames frames = report.Frames.ShouldNotBeNull();
        frames.Count.ShouldBe(header.FrameCount);
        frames.Rate.ShouldBe(header.FrameRate);
        header.FrameCount.ShouldBeGreaterThan(1);
        report.GridBits.ShouldBe((int)header.GridBits);
        report.ColorScale.ShouldBe(header.ColorScale);
        BakeReportClip clip = report.Clip.ShouldNotBeNull();
        clip.LoopMode.ShouldBe(header.LoopMode.ToString());
        clip.DurationSeconds.ShouldBe(header.DurationSeconds);

        BakeReportCache reportCache = report.Cache.ShouldNotBeNull();
        reportCache.Chunks.Count.ShouldBe(header.Chunks.Length);
        for (int i = 0; i < header.Chunks.Length; i++)
        {
            GaussianCacheChunkInfo info = header.Chunks[i];
            BakeReportChunk chunk = reportCache.Chunks[i];
            chunk.Index.ShouldBe(info.Index);
            chunk.Kind.ShouldBe(info.Kind.ToString());
            chunk.SplatLevel.ShouldBe((int)info.SplatLevel);
            chunk.DriverLevel.ShouldBe((int)info.DriverLevel);
            chunk.TemporalLevel.ShouldBe((int)info.TemporalLevel);
            chunk.Bytes.ShouldBe(info.ByteLength);
            chunk.Sha256.ShouldBe(info.Sha256);
        }

        report.Outputs.Select(written => written.File).ShouldBe(LoopRendition.All.Select(rendition => rendition.FileName));
        foreach (LoopRendition rendition in LoopRendition.All)
            File.Exists(Path.Combine(output, rendition.FileName)).ShouldBeTrue(rendition.FileName);

        JsonElement written = ReadReport(output);
        written.GetProperty("status").GetString().ShouldBe("ready");
        written.GetProperty("cache").GetProperty("contentHash").GetString().ShouldBe(reportCache.ContentHash);
        written.GetProperty("input").GetProperty("sha256").GetString().ShouldNotBeNull().Length.ShouldBe(64);
    }

    private GaussianBakePipeline CreatePipeline(FakeRenderServices render)
        => new(render, GaussianCacheTestSphere.Meshopt, static (_, _) => Task.FromResult<IReadOnlyList<string>>([]));

    private BakeSplatPreviewOptions AvatarOptions(string output)
        => Options(_avatarPak, AvatarAssetPath, "avatar", output, "--splats", TestSplatCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static BakeSplatPreviewOptions Options(string pak, string asset, string kind, string output, params string[] extra)
    {
        string[] args = ["--bake-splat-preview", "--input", pak, "--asset", asset, "--kind", kind, "--output", output, .. extra];
        BakeSplatPreviewOptions.TryParse(args, out BakeSplatPreviewOptions? options, out string? error).ShouldBeTrue(error);
        return options.ShouldNotBeNull();
    }

    private static BakeReport CreateFullReport()
        => new()
        {
            EngineVersion = "1.0.0",
            MeshoptimizerVersion = "v1.3 9e1f07b159d3cb777f1c67ed31fc11fd117986f4",
            Kind = "avatar",
            Input = new BakeReportInput { Pak = "avatar.pak", Asset = AvatarAssetPath, Sha256 = new string('a', 64) },
            Clip = new BakeReportClip { Name = "idle", Source = "idle", DurationSeconds = 4.0f, LoopMode = "Loop" },
            Splats = new BakeReportSplats { Total = 50_000, Static = 0, PerLevel = [781, 6_250, 50_000] },
            Driver = new BakeReportDriver { VerticesPerLevel = [125, 1_000, 8_000], TrianglesPerLevel = [230, 1_980, 15_900] },
            Frames = new BakeReportFrames { Count = 120, Rate = 30.0f },
            GridBits = 11,
            ColorScale = 1.7f,
            Cache = new BakeReportCache
            {
                ContentHash = new string('b', 64),
                Chunk0Bytes = 180_000,
                TotalBytes = 4_100_000,
                Chunks =
                [
                    new BakeReportChunk { Index = 0, Kind = "Root", Bytes = 180_000, Sha256 = new string('c', 64) },
                ],
            },
            Outputs = [new BakeReportOutput { File = "loop-1080.webm", Height = 1080, Codec = "vp9", Bytes = 912_345 }],
            Skipped = [new BakeReportSkipped { Node = "Root/Sparks", Component = "ParticleEmitterComponent", Reason = "not-captured" }],
            Warnings = ["clip-truncated"],
        };

    private static string[] PropertyNames(JsonElement element)
        => [.. element.EnumerateObject().Select(property => property.Name)];

    private static JsonElement ReadReport(string directory)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, BakeReportWriter.FileName)));
        return document.RootElement.Clone();
    }

    private static string Sha256Of(string path)
        => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] RelativeFiles(string directory)
        => [.. Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path))
            .Order(StringComparer.Ordinal)];

    private static T ReadAsset<T>(string path) where T : class
    {
        CookedAssetBlob blob = CookedAssetEnvelope.Deserialize(File.ReadAllBytes(path));
        blob.Format.ShouldBe(CookedAssetFormat.RuntimeBinaryV1);
        return CookedBinarySerializer.Deserialize(typeof(T), blob.Payload).ShouldBeOfType<T>();
    }

    /// <summary>
    /// Renders nothing: each view is one shared mid-gray image, each loop file is empty, and content work runs on
    /// the calling thread.
    /// </summary>
    private sealed class FakeRenderServices : IBakeRenderServices
    {
        private const float MidGray = 0.5f;

        private float[]? _gray;

        public int CaptureCalls { get; private set; }

        public int ContentCalls { get; private set; }

        public int LoopCalls { get; private set; }

        public Task<float[][]> CaptureViewsAsync(
            CookedBakeInput input,
            IReadOnlyList<BakeCameraPose> poses,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            CaptureCalls++;
            if (_gray is null || _gray.Length != width * height * 4)
            {
                _gray = new float[width * height * 4];
                for (int i = 0; i < _gray.Length; i += 4)
                {
                    _gray[i] = MidGray;
                    _gray[i + 1] = MidGray;
                    _gray[i + 2] = MidGray;
                    _gray[i + 3] = 1.0f;
                }
            }

            float[][] images = new float[poses.Count][];
            Array.Fill(images, _gray);
            return Task.FromResult(images);
        }

        public Task RecordLoopAsync(
            CookedBakeInput input,
            GaussianCacheBuildResult cache,
            IReadOnlyList<BakeCameraPose> poses,
            float fps,
            FfmpegEncoder encoder,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            LoopCalls++;
            poses.Count.ShouldBeGreaterThan(0);
            Directory.CreateDirectory(outputDirectory);
            foreach (LoopRendition rendition in LoopRendition.All)
                File.WriteAllBytes(Path.Combine(outputDirectory, rendition.FileName), []);
            return Task.CompletedTask;
        }

        public Task<T> RunOnContentThreadAsync<T>(CookedBakeInput input, Func<T> work, CancellationToken cancellationToken)
        {
            ContentCalls++;
            return Task.FromResult(work());
        }
    }
}
