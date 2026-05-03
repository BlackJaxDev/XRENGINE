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

        fileName.ShouldBe("log_vulkan.txt");
    }

    [Test]
    public void BuildCategoryLogFileName_UsesDedicatedLightingAndMeshesNames()
    {
        XREngine.Debug.BuildCategoryLogFileName(XREngine.ELogCategory.Lighting).ShouldBe("log_lighting.txt");
        XREngine.Debug.BuildCategoryLogFileName(XREngine.ELogCategory.Meshes).ShouldBe("log_meshes.txt");
        XREngine.Debug.BuildCategoryLogFileName(XREngine.ELogCategory.Textures).ShouldBe("log_textures.txt");
    }

    [Test]
    public void NormalizeLogNameSegment_CollapsesPunctuationIntoSingleSeparators()
    {
        string normalized = XREngine.Debug.NormalizeLogNameSegment("  Editor Bootstrap / Trace  ");

        normalized.ShouldBe("editor-bootstrap-trace");
    }
}
