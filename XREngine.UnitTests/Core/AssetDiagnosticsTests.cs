using NUnit.Framework;
using Shouldly;
using XREngine.Diagnostics;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class AssetDiagnosticsTests
{
    [SetUp]
    public void SetUp()
    {
        AssetDiagnostics.ClearTrackedMissingAssets();
        AssetDiagnostics.ClearTrackedRebasedAssets();
    }

    [TearDown]
    public void TearDown()
    {
        AssetDiagnostics.ClearTrackedMissingAssets();
        AssetDiagnostics.ClearTrackedRebasedAssets();
    }

    [Test]
    public void RecordRebasedAsset_CapturesRepairKindAndSourceAssetPath()
    {
        string sourceAssetPath = Path.Combine(Path.GetTempPath(), "XRENGINE", "Shader.asset");

        AssetDiagnostics.RecordRebasedAsset(
            @"C:\OldWorkspace\Build\CommonAssets\Shaders\PointLightShadowDepth.fs",
            Path.Combine(Path.GetTempPath(), "XRENGINE", "Build", "CommonAssets", "Shaders", "PointLightShadowDepth.fs"),
            "TextFile",
            "test context",
            AssetDiagnostics.AssetReferenceRepairKind.FoundCurrentWorkspacePath,
            sourceAssetPath);

        var info = AssetDiagnostics.GetTrackedRebasedAssets().ShouldHaveSingleItem();
        info.Category.ShouldBe("TextFile");
        info.RepairKind.ShouldBe(AssetDiagnostics.AssetReferenceRepairKind.FoundCurrentWorkspacePath);
        info.LastContext.ShouldBe("test context");
        info.Count.ShouldBe(1);
        info.LastSourceAssetPath.ShouldBe(Path.GetFullPath(sourceAssetPath));
        info.SourceAssetPaths.ShouldContain(Path.GetFullPath(sourceAssetPath));
    }

    [Test]
    public void RecordRebasedAsset_AggregatesMultipleOwningAssets()
    {
        string sourceA = Path.Combine(Path.GetTempPath(), "XRENGINE", "A.asset");
        string sourceB = Path.Combine(Path.GetTempPath(), "XRENGINE", "B.asset");
        string resolvedPath = Path.Combine(Path.GetTempPath(), "XRENGINE", "Assets", "Shared.png");

        AssetDiagnostics.RecordRebasedAsset(
            @"C:\OldWorkspace\Assets\Shared.png",
            resolvedPath,
            "XRTexture2D",
            repairKind: AssetDiagnostics.AssetReferenceRepairKind.PathMadePortable,
            sourceAssetPath: sourceA);
        AssetDiagnostics.RecordRebasedAsset(
            @"C:\OldWorkspace\Assets\Shared.png",
            resolvedPath,
            "XRTexture2D",
            repairKind: AssetDiagnostics.AssetReferenceRepairKind.PathMadePortable,
            sourceAssetPath: sourceB);

        var info = AssetDiagnostics.GetTrackedRebasedAssets().ShouldHaveSingleItem();
        info.Count.ShouldBe(2);
        info.SourceAssetPaths.Count.ShouldBe(2);
        info.SourceAssetPaths.ShouldContain(Path.GetFullPath(sourceA));
        info.SourceAssetPaths.ShouldContain(Path.GetFullPath(sourceB));
    }
}
