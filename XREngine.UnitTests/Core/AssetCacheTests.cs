using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using XREngine;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering;
using TestContext = NUnit.Framework.TestContext;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class AssetCacheTests
{
    [Test]
    public void Load3rdPartyAsset_UsesCacheUntilSourceChanges()
    {
        using var sandbox = new AssetCacheSandbox();
        var manager = new AssetManager();
        manager.MonitorGameAssetsForChanges = false; // prevent FileSystemWatcher auto-imports from corrupting LoadCount
        try
        {
            manager.GameAssetsPath = sandbox.AssetsPath;
            manager.GameCachePath = sandbox.CachePath;

            string sourcePath = Path.Combine(sandbox.AssetsPath, "sample.stub");
            File.WriteAllText(sourcePath, "first");

            StubThirdPartyAsset.LoadCount = 0;

            var firstLoad = manager.Load<StubThirdPartyAsset>(sourcePath);
            firstLoad.ShouldNotBeNull();
            firstLoad.Payload.ShouldBe("first");
            StubThirdPartyAsset.LoadCount.ShouldBe(1);

            ClearAssetCaches(manager);

            var secondLoad = manager.Load<StubThirdPartyAsset>(sourcePath);
            secondLoad.ShouldNotBeNull();
            secondLoad.Payload.ShouldBe("first");
            StubThirdPartyAsset.LoadCount.ShouldBe(1, "cache should short-circuit repeated imports");

            File.WriteAllText(sourcePath, "updated");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(1));

            ClearAssetCaches(manager);

            var thirdLoad = manager.Load<StubThirdPartyAsset>(sourcePath);
            thirdLoad.ShouldNotBeNull();
            thirdLoad.Payload.ShouldBe("updated");
            StubThirdPartyAsset.LoadCount.ShouldBe(2, "modified sources must trigger re-imports");
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Test]
    public void LoadAnimationClip_GeneratesAndUsesThirdPartyCacheAsset()
    {
        using var sandbox = new AssetCacheSandbox();
        var manager = new AssetManager();
        manager.MonitorGameAssetsForChanges = false;
        try
        {
            manager.GameAssetsPath = sandbox.AssetsPath;
            manager.GameCachePath = sandbox.CachePath;

            string sourcePath = Path.Combine(sandbox.AssetsPath, "walk.anim");
            File.WriteAllText(sourcePath, """
AnimationClip:
  m_Name: CacheBypassClip
  m_SampleRate: 60
  m_AnimationClipSettings:
    m_StartTime: 0
    m_StopTime: 1
    m_LoopTime: 0
  m_FloatCurves: []
""");

            AnimationClip? clip = manager.Load<AnimationClip>(sourcePath);
            clip.ShouldNotBeNull();
            clip.LengthInSeconds.ShouldBe(1.0f);

            string[] cacheFiles = [.. Directory.EnumerateFiles(sandbox.CachePath, "*", SearchOption.AllDirectories)];
            cacheFiles.Length.ShouldBe(1, "animation clips should now emit a cache asset");
            string cachePath = cacheFiles[0];
            File.Exists(cachePath).ShouldBeTrue();
            string cacheText = File.ReadAllText(cachePath);
            cacheText.ShouldContain("Format: CookedBinary");
            cacheText.ShouldContain("Payload:");

            DateTime cacheTimestampUtc = File.GetLastWriteTimeUtc(cachePath);
            File.WriteAllText(sourcePath, "this is intentionally not valid animation data");
            File.SetLastWriteTimeUtc(sourcePath, cacheTimestampUtc.AddSeconds(-1));

            ClearAssetCaches(manager);

            AnimationClip? cachedClip = manager.Load<AnimationClip>(sourcePath);
            cachedClip.ShouldNotBeNull();
            cachedClip.Name.ShouldBe("CacheBypassClip");
            cachedClip.LengthInSeconds.ShouldBe(1.0f, "fresh cache should satisfy reloads without re-importing the source");
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Test]
    public void LoadEngineShaderAsset_LoadsShaderSourceAndCreatesEngineCacheAsset()
    {
        using var sandbox = new AssetCacheSandbox();
        var manager = new AssetManager();
        manager.MonitorGameAssetsForChanges = false;
        try
        {
            manager.GameAssetsPath = sandbox.AssetsPath;
            manager.GameCachePath = sandbox.CachePath;

            string shaderPath = manager.ResolveEngineAssetPath("Shaders", "Uber", "UberShader.frag");
            File.Exists(shaderPath).ShouldBeTrue();

            XRShader? shader = manager.Load<XRShader>(shaderPath);
            shader.ShouldNotBeNull();
            shader.FilePath.ShouldBe(shaderPath);
            shader.Source.FilePath.ShouldBe(shaderPath);
            string shaderSource = shader.Source.Text ?? string.Empty;
            shaderSource.Length.ShouldBeGreaterThan(0);
            shaderSource.ShouldContain("void main");

            string cacheRoot = Path.Combine(sandbox.CachePath, "Engine");
            string[] cacheFiles = [.. Directory.EnumerateFiles(cacheRoot, "*.asset", SearchOption.AllDirectories)];
            cacheFiles.ShouldContain(path => path.EndsWith("UberShader.frag.XREngine.Rendering.XRShader.asset", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Test]
    public void LoadEngineShaderAsset_FromCacheKeepsShaderInPathCacheWithoutTextFileCacheAsset()
    {
        using var sandbox = new AssetCacheSandbox();
        var manager = new AssetManager();
        manager.MonitorGameAssetsForChanges = false;
        try
        {
            manager.GameAssetsPath = sandbox.AssetsPath;
            manager.GameCachePath = sandbox.CachePath;

            string shaderPath = manager.ResolveEngineAssetPath("Shaders", "Scene3D", "PostProcess.fs");
            File.Exists(shaderPath).ShouldBeTrue();

            XRShader? firstLoad = manager.Load<XRShader>(shaderPath);
            firstLoad.ShouldNotBeNull();

            string cacheRoot = Path.Combine(sandbox.CachePath, "Engine");
            string[] cacheFiles = Directory.Exists(cacheRoot)
                ? [.. Directory.EnumerateFiles(cacheRoot, "*.asset", SearchOption.AllDirectories)]
                : [];

            cacheFiles.ShouldContain(path => path.EndsWith("PostProcess.fs.XREngine.Rendering.XRShader.asset", StringComparison.OrdinalIgnoreCase));
            cacheFiles.ShouldNotContain(path => path.EndsWith("PostProcess.fs.XREngine.Core.Files.TextFile.asset", StringComparison.OrdinalIgnoreCase));

            ClearAssetCaches(manager);

            XRShader? secondLoad = manager.Load<XRShader>(shaderPath);
            secondLoad.ShouldNotBeNull();
            secondLoad.Source.FilePath.ShouldBe(shaderPath);
            (secondLoad.Source.Text ?? string.Empty).ShouldContain("void");

            manager.TryGetAssetByPath(shaderPath, out XRAsset? cached).ShouldBeTrue();
            cached.ShouldBeOfType<XRShader>();
            cached.ShouldBeSameAs(secondLoad);

            string[] cacheFilesAfterReload = Directory.Exists(cacheRoot)
                ? [.. Directory.EnumerateFiles(cacheRoot, "*.asset", SearchOption.AllDirectories)]
                : [];
            cacheFilesAfterReload.ShouldNotContain(path => path.EndsWith("PostProcess.fs.XREngine.Core.Files.TextFile.asset", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Test]
    public void LoadEngineShaderAsset_IgnoresMismatchedPathCacheEntry()
    {
        using var sandbox = new AssetCacheSandbox();
        var manager = new AssetManager();
        manager.MonitorGameAssetsForChanges = false;
        try
        {
            manager.GameAssetsPath = sandbox.AssetsPath;
            manager.GameCachePath = sandbox.CachePath;

            string shaderPath = manager.ResolveEngineAssetPath("Shaders", "Scene3D", "PostProcess.fs");
            File.Exists(shaderPath).ShouldBeTrue();

            StubThirdPartyAsset staleAsset = new()
            {
                FilePath = shaderPath,
                OriginalPath = shaderPath,
            };
            manager.EnsureTracked(staleAsset);

            XRShader? shader = manager.Load<XRShader>(shaderPath);
            shader.ShouldNotBeNull();
            manager.TryGetAssetByPath(shaderPath, out XRAsset? cached).ShouldBeTrue();
            cached.ShouldBeOfType<XRShader>();
            cached.ShouldBeSameAs(shader);
            shader.Source.FilePath.ShouldBe(shaderPath);
            (shader.Source.Text ?? string.Empty).ShouldContain("void");
        }
        finally
        {
            manager.Dispose();
        }
    }

    private static void ClearAssetCaches(AssetManager manager)
    {
        manager.LoadedAssetsByPathInternal.Clear();
        manager.LoadedAssetsByOriginalPathInternal.Clear();
        manager.LoadedAssetsByIDInternal.Clear();
    }

    private sealed class AssetCacheSandbox : IDisposable
    {
        public string RootPath { get; }
        public string AssetsPath { get; }
        public string CachePath { get; }

        public AssetCacheSandbox()
        {
            RootPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "AssetCache", Guid.NewGuid().ToString("N"));
            AssetsPath = Path.Combine(RootPath, "Assets");
            CachePath = Path.Combine(RootPath, "Cache");
            Directory.CreateDirectory(AssetsPath);
            Directory.CreateDirectory(CachePath);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                    Directory.Delete(RootPath, recursive: true);
            }
            catch
            {
                // Ignored - test cleanup best-effort.
            }
        }
    }

    [XR3rdPartyExtensions("stub")]
    private sealed class StubThirdPartyAsset : XRAsset
    {
        public static int LoadCount;
        public string? Payload { get; set; }
        public override bool Load3rdParty(string filePath)
        {
            LoadCount++;
            Payload = File.ReadAllText(filePath);
            return true;
        }
    }
}
