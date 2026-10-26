using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Cli;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class BakeSplatPreviewOptionsTests
{
    private static readonly string[] MinimalArgs =
    [
        "--input", "in/avatar.pak",
        "--asset", "Fixtures/Avatar.asset",
        "--kind", "avatar",
        "--output", "out/avatar",
    ];

    [Test]
    public void TryParse_MinimalArgs_UsesDefaults()
    {
        BakeSplatPreviewOptions.TryParse(MinimalArgs, out BakeSplatPreviewOptions? options, out string? error).ShouldBeTrue(error);

        options.ShouldNotBeNull();
        error.ShouldBeNull();
        options.InputPak.ShouldBe("in/avatar.pak");
        options.AssetPath.ShouldBe("Fixtures/Avatar.asset");
        options.Kind.ShouldBe(GaussianBakeKind.Avatar);
        options.OutputDirectory.ShouldBe("out/avatar");
        options.ClipName.ShouldBeNull();
        options.ColorMode.ShouldBe("lit-projection");
        options.StagePak.ShouldBeNull();
        options.StageAsset.ShouldBeNull();
        options.FfmpegPath.ShouldNotBeNullOrWhiteSpace();
        options.H264Encoder.ShouldBe("openh264");
        options.SplatCount.ShouldBeNull();
        options.DumpViewsOnly.ShouldBeFalse();
    }

    [Test]
    public void TryParse_AllFlags_ReadsValues()
    {
        string[] args =
        [
            "--bake-splat-preview",
            .. MinimalArgs,
            "--clip", "Wave",
            "--color-mode", "optimized-fit",
            "--stage", "stages/studio.pak::Stages/Studio.asset",
            "--ffmpeg", "tools/ffmpeg.exe",
            "--h264-encoder", "nvenc",
            "--splats", "50000",
            "--dump-views-only",
        ];

        BakeSplatPreviewOptions.TryParse(args, out BakeSplatPreviewOptions? options, out string? error).ShouldBeTrue(error);

        options.ShouldNotBeNull();
        options.ClipName.ShouldBe("Wave");
        options.ColorMode.ShouldBe("optimized-fit");
        options.StagePak.ShouldBe("stages/studio.pak");
        options.StageAsset.ShouldBe("Stages/Studio.asset");
        options.FfmpegPath.ShouldBe("tools/ffmpeg.exe");
        options.H264Encoder.ShouldBe("nvenc");
        options.SplatCount.ShouldBe(50_000);
        options.DumpViewsOnly.ShouldBeTrue();
    }

    [TestCase("ITEM", GaussianBakeKind.Item)]
    [TestCase("world", GaussianBakeKind.World)]
    public void TryParse_KindName_IsCaseInsensitive(string kindValue, GaussianBakeKind expected)
    {
        string[] args = Replace(MinimalArgs, "--kind", kindValue);

        BakeSplatPreviewOptions.TryParse(args, out BakeSplatPreviewOptions? options, out string? error).ShouldBeTrue(error);

        options!.Kind.ShouldBe(expected);
    }

    [Test]
    public void TryParse_MissingInput_ReturnsErrorNamingFlag()
    {
        string[] args = Remove(MinimalArgs, "--input");

        BakeSplatPreviewOptions.TryParse(args, out BakeSplatPreviewOptions? options, out string? error).ShouldBeFalse();

        options.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("--input");
    }

    [TestCase("--input")]
    [TestCase("--asset")]
    [TestCase("--output")]
    public void TryParse_BlankRequiredValue_ReturnsErrorNamingFlag(string flag)
    {
        string[] args = Replace(MinimalArgs, flag, "   ");

        BakeSplatPreviewOptions.TryParse(args, out _, out string? error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain(flag);
    }

    [Test]
    public void TryParse_FlagWithoutValue_ReturnsErrorNamingFlag()
    {
        string[] args = [.. MinimalArgs, "--clip"];

        BakeSplatPreviewOptions.TryParse(args, out _, out string? error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("--clip");
    }

    [TestCase("prop")]
    [TestCase("1")]
    public void TryParse_UnknownKind_Fails(string kindValue)
    {
        string[] args = Replace(MinimalArgs, "--kind", kindValue);

        BakeSplatPreviewOptions.TryParse(args, out BakeSplatPreviewOptions? options, out string? error).ShouldBeFalse();

        options.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("--kind");
    }

    [Test]
    public void TryParse_StageWithoutSeparator_Fails()
    {
        string[] args = [.. MinimalArgs, "--stage", "stage.pak"];

        BakeSplatPreviewOptions.TryParse(args, out BakeSplatPreviewOptions? options, out string? error).ShouldBeFalse();

        options.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("--stage");
        error.ShouldContain("::");
    }

    [TestCase("::Stages/Studio.asset")]
    [TestCase("stage.pak::")]
    public void TryParse_StageWithBlankPart_Fails(string stageValue)
    {
        string[] args = [.. MinimalArgs, "--stage", stageValue];

        BakeSplatPreviewOptions.TryParse(args, out _, out string? error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("--stage");
    }

    [Test]
    public void TryParse_SplatsAboveKindBudget_Fails()
    {
        string[] args = [.. MinimalArgs, "--splats", "60000"];

        BakeSplatPreviewOptions.TryParse(args, out BakeSplatPreviewOptions? options, out string? error).ShouldBeFalse();

        options.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("--splats");
        error.ShouldContain("50000");
    }

    [Test]
    public void TryParse_WorldSplatsWithinWorldBudget_Succeeds()
    {
        string[] args = [.. Replace(MinimalArgs, "--kind", "world"), "--splats", "300000"];

        BakeSplatPreviewOptions.TryParse(args, out BakeSplatPreviewOptions? options, out string? error).ShouldBeTrue(error);

        options!.SplatCount.ShouldBe(300_000);
    }

    [TestCase("0")]
    [TestCase("-5")]
    [TestCase("many")]
    public void TryParse_SplatsNotPositive_Fails(string splatValue)
    {
        string[] args = [.. MinimalArgs, "--splats", splatValue];

        BakeSplatPreviewOptions.TryParse(args, out _, out string? error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("--splats");
    }

    [TestCase("--color-mode", "albedo")]
    [TestCase("--h264-encoder", "libx264")]
    public void TryParse_UnknownChoice_ReturnsErrorNamingFlag(string flag, string value)
    {
        string[] args = [.. MinimalArgs, flag, value];

        BakeSplatPreviewOptions.TryParse(args, out _, out string? error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain(flag);
    }

    [Test]
    public void TryParse_UnknownFlag_ReturnsErrorNamingFlag()
    {
        string[] args = [.. MinimalArgs, "--frames", "10"];

        BakeSplatPreviewOptions.TryParse(args, out _, out string? error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("--frames");
    }

    [Test]
    public void TryParse_RepeatedFlag_ReturnsErrorNamingFlag()
    {
        string[] args = [.. MinimalArgs, "--kind", "item"];

        BakeSplatPreviewOptions.TryParse(args, out _, out string? error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("--kind");
    }

    private static string[] Remove(string[] args, string flag)
    {
        List<string> result = [];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == flag)
            {
                i++;
                continue;
            }

            result.Add(args[i]);
        }

        return [.. result];
    }

    private static string[] Replace(string[] args, string flag, string value)
    {
        string[] result = [.. args];
        int index = Array.IndexOf(result, flag);
        result[index + 1] = value;
        return result;
    }
}
