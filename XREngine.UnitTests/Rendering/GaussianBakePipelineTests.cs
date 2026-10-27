using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Lights;
using XREngine.Core.Files;
using XREngine.Editor.GaussianBake;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
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
using XREngine.Scene.Transforms;
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

    /// <summary>
    /// A chunk 0 limit between the measured chunk 0 sizes of the test avatar at <see cref="TestSplatCount"/> splats:
    /// 7,547 bytes at the level 0 target 2,000 (levels 500 and 4,000) and 1,922 bytes at the target 250 (levels 63,
    /// 500, and 4,000).
    /// </summary>
    private const int RetryChunk0Bytes = 4_700;

    /// <summary>A chunk 0 limit below the measured chunk 0 size of the test avatar at the level 0 target 31: 1,414 bytes.</summary>
    private const int FailChunk0Bytes = 1_000;

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
        // The final folder is the output of an earlier bake.
        string final = Path.Combine(_workRoot, "commit");
        WritePreviousOutput(final);

        BakeOutputDirectory output = BakeOutputDirectory.Begin(final);
        output.StagingPath.ShouldStartWith(Path.GetFullPath(final) + ".staging-");
        Directory.Exists(output.StagingPath).ShouldBeTrue();
        File.WriteAllText(Path.Combine(output.StagingPath, "new.txt"), "fresh");
        output.Commit();

        File.Exists(Path.Combine(final, "old.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(final, BakeReportWriter.FileName)).ShouldBeFalse();
        File.ReadAllText(Path.Combine(final, "new.txt")).ShouldBe("fresh");
        Directory.Exists(output.StagingPath).ShouldBeFalse();
    }

    [Test]
    public void OutputDirectory_MissingFolder_CommitMakesIt()
    {
        string final = Path.Combine(_workRoot, "missing-parent", "out");

        BakeOutputDirectory output = BakeOutputDirectory.Begin(final);
        File.WriteAllText(Path.Combine(output.StagingPath, "new.txt"), "fresh");
        output.Commit();

        Directory.GetFileSystemEntries(final).Select(Path.GetFileName).ShouldBe(["new.txt"]);
        StagingFoldersOf(final).ShouldBeEmpty();
    }

    [Test]
    public void OutputDirectory_EmptyFolder_CommitReplacesIt()
    {
        string final = Path.Combine(_workRoot, "empty-parent", "out");
        Directory.CreateDirectory(final);

        BakeOutputDirectory output = BakeOutputDirectory.Begin(final);
        File.WriteAllText(Path.Combine(output.StagingPath, "new.txt"), "fresh");
        output.Commit();

        Directory.GetFileSystemEntries(final).Select(Path.GetFileName).ShouldBe(["new.txt"]);
        StagingFoldersOf(final).ShouldBeEmpty();
    }

    [Test]
    public void OutputDirectory_UnrelatedFolder_BeginRefusesAndDeletesNothing()
    {
        string final = Path.Combine(_workRoot, "unrelated-parent", "out");
        Directory.CreateDirectory(Path.Combine(final, "sub"));
        File.WriteAllText(Path.Combine(final, "notes.txt"), "keep");

        GaussianBakeException refusal = Should.Throw<GaussianBakeException>(() => BakeOutputDirectory.Begin(final));

        refusal.FailureCode.ShouldBe(GaussianBakeFailureCodes.InvalidArguments);
        refusal.Message.ShouldContain(Path.GetFullPath(final));
        Directory.GetFileSystemEntries(final).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(["notes.txt", "sub"]);
        File.ReadAllText(Path.Combine(final, "notes.txt")).ShouldBe("keep");
        StagingFoldersOf(final).ShouldBeEmpty();
    }

    [Test]
    public void OutputDirectory_FileAtFinalPath_BeginRefuses()
    {
        string parent = Path.Combine(_workRoot, "file-parent");
        Directory.CreateDirectory(parent);
        string final = Path.Combine(parent, "out");
        File.WriteAllText(final, "keep");

        Should.Throw<GaussianBakeException>(() => BakeOutputDirectory.Begin(final))
            .FailureCode.ShouldBe(GaussianBakeFailureCodes.InvalidArguments);

        File.ReadAllText(final).ShouldBe("keep");
        StagingFoldersOf(final).ShouldBeEmpty();
    }

    [Test]
    public void OutputDirectory_FolderBecomesUnrelated_FailDeletesOnlyStaging()
    {
        string final = Path.Combine(_workRoot, "late-unrelated-parent", "out");
        Directory.CreateDirectory(final);
        BakeOutputDirectory output = BakeOutputDirectory.Begin(final);
        File.WriteAllText(Path.Combine(output.StagingPath, "partial.bin"), "partial");
        File.WriteAllText(Path.Combine(final, "notes.txt"), "keep");
        BakeReport report = CreateFullReport();
        report.MarkFailed(GaussianBakeFailureCodes.InputLoadFailed, "missing");

        Should.Throw<GaussianBakeException>(() => output.Fail(report))
            .FailureCode.ShouldBe(GaussianBakeFailureCodes.InvalidArguments);

        Directory.GetFileSystemEntries(final).Select(Path.GetFileName).ShouldBe(["notes.txt"]);
        Directory.Exists(output.StagingPath).ShouldBeFalse();
    }

    [Test]
    public async Task Pipeline_UnrelatedOutputFolder_FailsInvalidArgumentsAndWritesNothing()
    {
        FakeRenderServices render = new();
        string final = Path.Combine(_workRoot, "unrelated-pipeline-parent", "out");
        Directory.CreateDirectory(final);
        File.WriteAllText(Path.Combine(final, "dummy.txt"), "keep");

        // The asset path is wrong too: the output folder check comes first and nothing is deleted.
        BakeReport report = await CreatePipeline(render).RunAsync(
            Options(Path.Combine(_workRoot, "does-not-exist.pak"), "Fixtures/Typo.asset", "item", final),
            CancellationToken.None);

        report.Status.ShouldBe(BakeReport.FailedStatus);
        report.FailureCode.ShouldBe(GaussianBakeFailureCodes.InvalidArguments);
        GaussianBakeFailureCodes.ExitCodeFor(report.FailureCode!).ShouldBe(2);
        render.ContentCalls.ShouldBe(0);
        Directory.GetFileSystemEntries(final).Select(Path.GetFileName).ShouldBe(["dummy.txt"]);
        File.ReadAllText(Path.Combine(final, "dummy.txt")).ShouldBe("keep");
        StagingFoldersOf(final).ShouldBeEmpty();
    }

    [Test]
    public void OutputDirectory_Fail_LeavesOnlyReport()
    {
        string final = Path.Combine(_workRoot, "fail");
        WritePreviousOutput(final);
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

    [Test]
    public async Task Pipeline_PreparedAvatar_RunsOneTime()
    {
        FakeRenderServices render = new();
        string output = Path.Combine(_workRoot, "avatar-prepared");

        GaussianBakePreparation preparation = await GaussianBakePipeline.PrepareAsync(
            AvatarOptions(output),
            GaussianCacheTestSphere.Meshopt,
            static (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
            CancellationToken.None);

        preparation.IsPrepared.ShouldBeTrue(preparation.Report.FailureDetail);
        preparation.Input.ShouldNotBeNull();
        render.ContentCalls.ShouldBe(0);
        BakeReport report = await CreatePipeline(render).RunAsync(preparation, CancellationToken.None);
        report.Status.ShouldBe(BakeReport.ReadyStatus, report.FailureDetail);
        preparation.IsPrepared.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => CreatePipeline(render).RunAsync(preparation, CancellationToken.None));
    }

    [Test]
    public async Task Preparation_ReportCannotBeWritten_HasNoReportPath()
    {
        string final = Path.Combine(_workRoot, "locked-parent", "out");
        WritePreviousOutput(final);
        GaussianBakePreparation preparation;

        // The open report blocks the delete of the output folder, so the failed report cannot be written.
        using (new FileStream(Path.Combine(final, BakeReportWriter.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            preparation = await PrepareAsync(Options(Path.Combine(_workRoot, "does-not-exist.pak"), AvatarAssetPath, "avatar", final));
        }

        preparation.IsPrepared.ShouldBeFalse();
        preparation.Report.FailureCode.ShouldBe(GaussianBakeFailureCodes.InputLoadFailed);
        preparation.ReportPath.ShouldBeNull();
        StagingFoldersOf(final).ShouldBeEmpty();
    }

    [Test]
    public async Task Preparation_FailedLoad_ReportPathNamesTheWrittenReport()
    {
        string final = Path.Combine(_workRoot, "failed-load-out");

        GaussianBakePreparation preparation = await PrepareAsync(Options(Path.Combine(_workRoot, "does-not-exist.pak"), AvatarAssetPath, "avatar", final));

        preparation.IsPrepared.ShouldBeFalse();
        preparation.ReportPath.ShouldBe(Path.Combine(Path.GetFullPath(final), BakeReportWriter.FileName));
        ReadReport(final).GetProperty("failureCode").GetString().ShouldBe("input-load-failed");
    }

    [Test]
    public async Task Preparation_UnrelatedOutputFolder_HasNoReportPath()
    {
        string final = Path.Combine(_workRoot, "unrelated-preparation-parent", "out");
        Directory.CreateDirectory(final);
        File.WriteAllText(Path.Combine(final, "dummy.txt"), "keep");

        GaussianBakePreparation preparation = await PrepareAsync(AvatarOptions(final));

        preparation.IsPrepared.ShouldBeFalse();
        preparation.Report.FailureCode.ShouldBe(GaussianBakeFailureCodes.InvalidArguments);
        preparation.ReportPath.ShouldBeNull();
        Directory.GetFileSystemEntries(final).Select(Path.GetFileName).ShouldBe(["dummy.txt"]);
    }

    [Test]
    public async Task Pipeline_ReadyBake_ReportPathNamesTheCommittedReport()
    {
        string final = Path.Combine(_workRoot, "ready-report-path-out");
        GaussianBakePreparation preparation = await PrepareAsync(AvatarOptions(final));
        preparation.ReportPath.ShouldBeNull();

        BakeReport report = await CreatePipeline(new FakeRenderServices()).RunAsync(preparation, CancellationToken.None);

        report.Status.ShouldBe(BakeReport.ReadyStatus, report.FailureDetail);
        preparation.ReportPath.ShouldBe(Path.Combine(Path.GetFullPath(final), BakeReportWriter.FileName));
        File.Exists(preparation.ReportPath).ShouldBeTrue();
    }

    [Test]
    public async Task CommandStage_MissingStagePak_LeavesOnlyFailedReport()
    {
        // The output folder holds a ready bake: after the stage failure it must hold only the failed report.
        string final = Path.Combine(_workRoot, "stage-parent", "out");
        WritePreviousOutput(final);
        string missingStage = Path.Combine(_workRoot, "missing-stage.pak");
        GaussianBakePreparation preparation = await PrepareAsync(
            AvatarOptions(final, "--stage", missingStage + BakeSplatPreviewOptions.StageSeparator + "Fixtures/Stage.asset"));
        preparation.IsPrepared.ShouldBeTrue(preparation.Report.FailureDetail);
        string inputSha = preparation.Report.Input.ShouldNotBeNull().Sha256.ShouldNotBeNull();
        StagingFoldersOf(final).Length.ShouldBe(1);

        SceneNode? stage = BakeSplatPreviewCommand.ResolveStage(preparation);

        stage.ShouldBeNull();
        preparation.IsPrepared.ShouldBeFalse();
        preparation.Report.FailureCode.ShouldBe(GaussianBakeFailureCodes.InputLoadFailed);
        preparation.ReportPath.ShouldBe(Path.Combine(Path.GetFullPath(final), BakeReportWriter.FileName));
        Directory.GetFileSystemEntries(final).Select(Path.GetFileName).ShouldBe([BakeReportWriter.FileName]);
        StagingFoldersOf(final).ShouldBeEmpty();
        JsonElement written = ReadReport(final);
        written.GetProperty("status").GetString().ShouldBe("failed");
        written.GetProperty("failureCode").GetString().ShouldBe("input-load-failed");
        written.GetProperty("failureDetail").GetString().ShouldNotBeNull().ShouldContain(missingStage);
        written.GetProperty("input").GetProperty("sha256").GetString().ShouldBe(inputSha);
    }

    [Test]
    public async Task Pipeline_Chunk0TooLarge_RetriesWithSmallerLevel0Target()
    {
        // A limit between the chunk 0 sizes at the level 0 targets 2,000 and 250 fails the first build and passes the
        // first build again.
        GaussianBakeBudget budget = GaussianBakeBudget.For(GaussianBakeKind.Avatar) with { Chunk0Bytes = RetryChunk0Bytes };
        string output = Path.Combine(_workRoot, "chunk0-retry-out");

        BakeReport report = await CreatePipeline(new FakeRenderServices(), budget).RunAsync(AvatarOptions(output), CancellationToken.None);

        report.Status.ShouldBe(BakeReport.ReadyStatus, report.FailureDetail);
        report.Warnings.ShouldBe([GaussianBakePipeline.Chunk0RetryWarning + "250"]);
        report.Cache.ShouldNotBeNull().Chunk0Bytes.ShouldBeLessThanOrEqualTo(RetryChunk0Bytes);
        report.Splats.ShouldNotBeNull().PerLevel[0].ShouldBeLessThanOrEqualTo(250);
        ReadReport(output).GetProperty("warnings")[0].GetString().ShouldBe("chunk0-retry:250");
    }

    [Test]
    public async Task Pipeline_Chunk0TooLargeAfterTwoRetries_FailsBudgetExceeded()
    {
        // The limit is smaller than chunk 0 at the level 0 target 31, the target of the second build again.
        GaussianBakeBudget budget = GaussianBakeBudget.For(GaussianBakeKind.Avatar) with { Chunk0Bytes = FailChunk0Bytes };
        FakeRenderServices render = new();
        string output = Path.Combine(_workRoot, "chunk0-fail-out");

        BakeReport report = await CreatePipeline(render, budget).RunAsync(AvatarOptions(output), CancellationToken.None);

        report.Status.ShouldBe(BakeReport.FailedStatus);
        report.FailureCode.ShouldBe(GaussianBakeFailureCodes.BudgetExceeded);
        report.FailureDetail.ShouldNotBeNull().ShouldStartWith(GaussianCacheBuilder.Chunk0Limit + ":");
        report.Warnings.ShouldBe([GaussianBakePipeline.Chunk0RetryWarning + "250", GaussianBakePipeline.Chunk0RetryWarning + "31"]);
        render.LoopCalls.ShouldBe(0);
        Directory.GetFileSystemEntries(output).Select(Path.GetFileName).ShouldBe([BakeReportWriter.FileName]);
        JsonElement written = ReadReport(output);
        written.GetProperty("failureCode").GetString().ShouldBe("budget-exceeded");
        written.GetProperty("failureDetail").GetString().ShouldNotBeNull().ShouldStartWith("chunk0:");
    }

    [Test]
    public void HasRenderableMesh_MeshUnderInactiveParent_IsFalse()
    {
        SceneNode root = new("Root", new Transform());
        SceneNode parent = new(root, "Parent", new Transform());
        SceneNode child = new(parent, "Child", new Transform());
        BakeMotionTestScenes.AddStaticCube(child);

        GaussianBakePipeline.HasRenderableMesh([root]).ShouldBeTrue();
        parent.IsActiveSelf = false;
        GaussianBakePipeline.HasRenderableMesh([root]).ShouldBeFalse();
    }

    [Test]
    public void FindCameraPath_PathUnderInactiveParent_IsSkipped()
    {
        SceneNode root = new("Root", new Transform());
        SceneNode hiddenParent = new(root, "HiddenParent", new Transform());
        SceneNode hidden = new(hiddenParent, "Hidden", new Transform());
        SceneNode shown = new(root, "Shown", new Transform());
        GaussianCacheCameraKey hiddenKey = new(0.0f, Vector3.UnitX, Quaternion.Identity);
        GaussianCacheCameraKey shownKey = new(1.0f, Vector3.UnitY, Quaternion.Identity);
        hidden.AddComponent<PreviewCameraPathComponent>()!.Keys = [hiddenKey];
        shown.AddComponent<PreviewCameraPathComponent>()!.Keys = [shownKey];
        hiddenParent.IsActiveSelf = false;

        GaussianBakePipeline.FindCameraPath([root]).ShouldBe([shownKey]);
    }

    private static Task<GaussianBakePreparation> PrepareAsync(BakeSplatPreviewOptions options)
        => GaussianBakePipeline.PrepareAsync(
            options,
            GaussianCacheTestSphere.Meshopt,
            static (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
            CancellationToken.None);

    private GaussianBakePipeline CreatePipeline(FakeRenderServices render)
        => new(render, GaussianCacheTestSphere.Meshopt, static (_, _) => Task.FromResult<IReadOnlyList<string>>([]));

    private static GaussianBakePipeline CreatePipeline(FakeRenderServices render, GaussianBakeBudget budget)
        => new(render, GaussianCacheTestSphere.Meshopt, static (_, _) => Task.FromResult<IReadOnlyList<string>>([]), _ => budget);

    private BakeSplatPreviewOptions AvatarOptions(string output, params string[] extra)
        => Options(_avatarPak, AvatarAssetPath, "avatar", output, ["--splats", TestSplatCount.ToString(System.Globalization.CultureInfo.InvariantCulture), .. extra]);

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

    /// <summary>Makes a folder that holds the outputs of an earlier bake: a ready report and a stale file.</summary>
    private static void WritePreviousOutput(string directory)
    {
        Directory.CreateDirectory(directory);
        BakeReportWriter.Write(CreateFullReport(), directory);
        File.WriteAllText(Path.Combine(directory, "old.txt"), "stale");
    }

    /// <summary>Returns the staging folders next to a final output folder.</summary>
    private static string[] StagingFoldersOf(string final)
    {
        string full = Path.GetFullPath(final);
        string parent = Path.GetDirectoryName(full)!;
        return Directory.Exists(parent)
            ? Directory.GetDirectories(parent, Path.GetFileName(full) + BakeOutputDirectory.StagingSuffix + "*")
            : [];
    }

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
