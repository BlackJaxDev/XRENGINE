using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Audio;

public sealed class Audio2Face3DComponentTests
{
    [Test]
    public void ResolveAnimationCsvPath_UsesProjectDirectoryForRelativePaths()
    {
        string resolved = Audio2Face3DComponent.ResolveAnimationCsvPath("Assets/Audio2Face/test.csv", @"C:\Projects\Demo", @"C:\Fallback");

        resolved.ShouldBe(@"C:\Projects\Demo\Assets\Audio2Face\test.csv");
    }

    [Test]
    public void TryUpdateLiveFrame_RejectsMismatchedBlendshapeCounts()
    {
        var component = new Audio2Face3DComponent();

        bool updated = component.TryUpdateLiveFrame(["JawOpen"], [0.25f, 0.5f], out string? error);

        updated.ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("weight count must match");
    }

    [Test]
    public void TryConnectLiveClient_WithoutRegisteredAdapter_ReturnsHelpfulError()
    {
        var component = new Audio2Face3DComponent
        {
            SourceMode = EAudio2Face3DSourceMode.LiveStream,
        };

        bool connected = component.TryConnectLiveClient();

        connected.ShouldBeFalse();
        component.LastLiveError.ShouldContain("No Audio2Face-3D live client adapter is registered");
    }

    [Test]
    public void TryUpdateLiveEmotionFrame_RejectsUnknownEmotionChannel()
    {
        var component = new Audio2Face3DComponent();

        bool updated = component.TryUpdateLiveEmotionFrame(["confused"], [0.5f], out string? error);

        updated.ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("Unsupported Audio2Emotion channel");
    }

    [Test]
    public void TryParseCsvText_ParsesBlendshapeFrames()
    {
        const string csv = "timecode,EyeBlinkLeft,JawOpen\n0.0,0.0,0.25\n0.5,1.0,0.75\n";

        bool parsed = Audio2Face3DComponent.TryParseCsvText(csv, out var animation, out var error);

        parsed.ShouldBeTrue(error);
        animation.ShouldNotBeNull();
        animation.BlendshapeNames.ShouldBe(["EyeBlinkLeft", "JawOpen"]);
        animation.EmotionNames.ShouldBeEmpty();
        animation.FrameCount.ShouldBe(2);
        animation.Duration.ShouldBe(0.5f, 0.000001f);
    }

    [Test]
    public void TryParseCsvText_SplitsEmotionColumnsFromBlendshapeColumns()
    {
        const string csv = "timecode,JawOpen,happy,sad\n0.0,0.25,0.0,1.0\n0.5,0.75,0.5,0.25\n";
        var animation = Audio2Face3DComponent.ParseCsvText(csv);
        float[] blendshapeOutput = new float[1];
        float[] emotionOutput = new float[Audio2Face3DEmotions.Count];

        animation.BlendshapeNames.ShouldBe(["JawOpen"]);
        animation.EmotionNames.ShouldBe(["happy", "sad"]);

        animation.Sample(0.25f, blendshapeOutput);
        animation.SampleEmotions(0.25f, emotionOutput);

        blendshapeOutput[0].ShouldBe(0.5f, 0.000001f);
        emotionOutput[(int)EAudio2Face3DEmotion.Happy].ShouldBe(0.25f, 0.000001f);
        emotionOutput[(int)EAudio2Face3DEmotion.Sad].ShouldBe(0.625f, 0.000001f);
        emotionOutput[(int)EAudio2Face3DEmotion.Angry].ShouldBe(0.0f, 0.000001f);
    }

    [Test]
    public void ParseCsvText_SampleInterpolatesBetweenFrames()
    {
        const string csv = "timecode,EyeBlinkLeft,JawOpen\n0.0,0.0,0.25\n0.5,1.0,0.75\n";
        var animation = Audio2Face3DComponent.ParseCsvText(csv);
        float[] output = new float[2];

        animation.Sample(0.25f, output);

        output[0].ShouldBe(0.5f, 0.000001f);
        output[1].ShouldBe(0.5f, 0.000001f);
    }

    [Test]
    public void TryParseCsvText_RejectsOutOfOrderFrames()
    {
        const string csv = "timecode,EyeBlinkLeft\n0.5,0.0\n0.25,1.0\n";

        bool parsed = Audio2Face3DComponent.TryParseCsvText(csv, out _, out var error);

        parsed.ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("earlier than the previous frame time");
    }
}