using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.UnitTests.Cutscenes;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// A cook can write <see cref="XRAsset.OriginalPath"/> as a portable <c>game://</c> reference.
/// The texture streaming restore must resolve it against the asset roots before any file system call.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class PortableTextureSourcePathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "portable-texture-source", "Assets");

    [Test]
    public void TryResolveFileSystemPath_ResolvesAGameReference_AgainstTheGameAssetRoot()
    {
        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(Root));

        AssetReferencePath.TryResolveFileSystemPath("game://Textures/floor.png", out string? path).ShouldBeTrue();

        path.ShouldBe(Path.Combine(Path.GetFullPath(Root), "Textures", "floor.png"));
    }

    [Test]
    public void TryResolveFileSystemPath_ReturnsAPathThatIsNotPortable_Unchanged()
    {
        string absolute = Path.Combine(Root, "Textures", "floor.png");

        AssetReferencePath.TryResolveFileSystemPath(absolute, out string? path).ShouldBeTrue();

        path.ShouldBe(absolute);
    }

    [Test]
    public void TryResolveFileSystemPath_RejectsAReferenceWithNoRoot_OrOutsideItsRoot()
    {
        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(Root));

        AssetReferencePath.TryResolveFileSystemPath("engine://Textures/floor.png", out _).ShouldBeFalse();
        AssetReferencePath.TryResolveFileSystemPath("game://../escape.png", out _).ShouldBeFalse();
    }

    [Test]
    public void TryRestoreImportedTextureStreamingSource_WithAPortableOriginalPath_RegistersTheResolvedSourceFile()
    {
        IRuntimeRenderingHostServices? previousRendering = RuntimeRenderingHostServices.Current;
        XRTexture2D texture = new() { OriginalPath = "game://Textures/floor.png" };
        try
        {
            RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
            using RendererBackendCatalog catalog = new();
            using IDisposable registrations = BuiltInRendererBackendModules.RegisterAll(catalog);
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(Root));

            texture.TryRestoreImportedTextureStreamingSource().ShouldBeTrue();

            texture.FilePath.ShouldBe(Path.Combine(Path.GetFullPath(Root), "Textures", "floor.png"));
            texture.OriginalPath.ShouldBe("game://Textures/floor.png");
        }
        finally
        {
            texture.Destroy(now: true);
            RuntimeRenderingHostServices.Current = previousRendering!;
        }
    }

    [Test]
    public void TryRestoreImportedTextureStreamingSource_WithAPortableReferenceThatDoesNotResolve_RegistersNothing()
    {
        IRuntimeRenderingHostServices? previousRendering = RuntimeRenderingHostServices.Current;
        XRTexture2D texture = new() { OriginalPath = "engine://Textures/floor.png" };
        try
        {
            RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(Root));

            texture.TryRestoreImportedTextureStreamingSource().ShouldBeFalse();

            texture.FilePath.ShouldBeNull();
        }
        finally
        {
            texture.Destroy(now: true);
            RuntimeRenderingHostServices.Current = previousRendering!;
        }
    }
}
