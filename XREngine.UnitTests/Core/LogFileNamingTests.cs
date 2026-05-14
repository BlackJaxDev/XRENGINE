using System;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class LogFileNamingTests
{
    [Test]
    public void BuildLogSessionId_UsesReadableTimestampAndProcessLabel()
    {
        DateTime timestamp = new(2026, 3, 17, 15, 51, 25);

        string sessionId = XREngine.Debug.BuildLogSessionId("XREngine.Editor.exe", timestamp, 16872);

        sessionId.ShouldBe("xrengine-editor-exe_2026-03-17_15-51-25_pid16872");
    }

    [Test]
    public void BuildCategoryLogFileName_UsesStableCategoryNameWithoutSessionSuffix()
    {
        string fileName = XREngine.Debug.BuildCategoryLogFileName(XREngine.ELogCategory.Vulkan);

        fileName.ShouldBe("log_vulkan.log");
    }

    [Test]
    public void BuildCategoryLogFileName_UsesDedicatedLightingAndMeshesNames()
    {
        XREngine.Debug.BuildCategoryLogFileName(XREngine.ELogCategory.Lighting).ShouldBe("log_lighting.log");
        XREngine.Debug.BuildCategoryLogFileName(XREngine.ELogCategory.Meshes).ShouldBe("log_meshes.log");
        XREngine.Debug.BuildCategoryLogFileName(XREngine.ELogCategory.Textures).ShouldBe("log_textures.log");
    }

    [Test]
    public void BuildCategoryLogHeader_IncludesCategoryAndDefaultColor()
    {
        DateTimeOffset timestamp = new(2026, 3, 17, 15, 51, 25, TimeSpan.FromHours(-7));

        string header = XREngine.Debug.BuildCategoryLogHeader(XREngine.ELogCategory.Textures, timestamp);

        header.ShouldBe("Log (textures) started 2026-03-17 15:51:25.000 -07:00 category=Textures color=#73E6F2");
    }

    [Test]
    public void BuildCategoryLogLine_UsesBootstrapStyleCategoryTag()
    {
        DateTimeOffset timestamp = new(2026, 3, 17, 15, 51, 25, TimeSpan.FromHours(-7));

        string line = XREngine.Debug.BuildCategoryLogLine(
            XREngine.ELogCategory.Textures,
            "[Texture.CacheHit] source='sponza.png'",
            timestamp);

        line.ShouldBe("2026-03-17 15:51:25.000 -07:00 [textures] [Texture.CacheHit] source='sponza.png'");
    }

    [Test]
    public void NormalizeLogNameSegment_CollapsesPunctuationIntoSingleSeparators()
    {
        string normalized = XREngine.Debug.NormalizeLogNameSegment("  Editor Bootstrap / Trace  ");

        normalized.ShouldBe("editor-bootstrap-trace");
    }
}
