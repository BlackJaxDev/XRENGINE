using NUnit.Framework;
using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using XREngine;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Core.Files.Caching;
using XREngine.Data;
using XREngine.Rendering;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Prefabs;
using TestContext = NUnit.Framework.TestContext;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class AssetCacheTests
{
    [Test]
    [NonParallelizable]
    public void ResolveTextureStreamingAuthorityPath_WhenWarmupDisabled_DoesNotWriteCache()
    {
        using var sandbox = new AssetCacheSandbox();
        var manager = new AssetManager
        {
            MonitorGameAssetsForChanges = false,
            GameAssetsPath = sandbox.AssetsPath,
            GameCachePath = sandbox.CachePath,
        };
        string variableName = XREngineEnvironmentVariables.TextureStreamingCacheWarmupEnabled;
        string? previousValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, "false");
            string sourcePath = Path.Combine(sandbox.AssetsPath, "bounded-cache.png");
            File.WriteAllBytes(sourcePath,
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            ]);

            string authorityPath = manager.ResolveTextureStreamingAuthorityPath(sourcePath);

            authorityPath.ShouldBe(Path.GetFullPath(sourcePath));
            Directory.EnumerateFiles(sandbox.CachePath, "*", SearchOption.AllDirectories)
                .ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
            manager.Dispose();
        }
    }

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
    public void ModelBinaryCacheCodec_ClaimsPrefabSourcesAndRejectsLegacyYaml()
    {
        using var sandbox = new AssetCacheSandbox();
        string cachePath = Path.Combine(sandbox.CachePath, "legacy-model.asset");
        File.WriteAllText(cachePath, "_type: XREngine.Scene.Prefabs.XRPrefabSource");

        var codec = new ModelBinaryCacheCodec();

        codec.GetOwnership(typeof(StubPrefabSource)).ShouldBe(CacheCodecOwnership.Exclusive);
        codec.GetOwnership(typeof(StubThirdPartyAsset)).ShouldBe(CacheCodecOwnership.NotHandled);
        AssetManager.GetThirdPartyCacheCodecOwnership(typeof(StubPrefabSource))
            .ShouldBe(CacheCodecOwnership.Exclusive);

        CacheReadResult readResult = codec.Read(
            cachePath,
            Path.Combine(sandbox.AssetsPath, "source.modelstub"),
            DateTime.UtcNow);

        readResult.Status.ShouldBe(CacheReadStatus.Rejected);
        readResult.Reason.ShouldBe(CacheRejectReason.LegacyFormat);
    }

    [Test]
    public void LoadPrefabSource_ExclusiveCodecDoesNotFallThroughToLegacyYaml()
    {
        using var sandbox = new AssetCacheSandbox();
        var manager = new AssetManager
        {
            MonitorGameAssetsForChanges = false,
            GameAssetsPath = sandbox.AssetsPath,
            GameCachePath = sandbox.CachePath,
        };

        try
        {
            string sourcePath = Path.Combine(sandbox.AssetsPath, "sample.modelstub");
            File.WriteAllText(sourcePath, "source-first");
            StubPrefabSource.LoadCount = 0;

            StubPrefabSource? firstLoad = manager.Load<StubPrefabSource>(sourcePath);
            firstLoad.ShouldNotBeNull();
            firstLoad.Payload.ShouldBe("source-first");
            StubPrefabSource.LoadCount.ShouldBe(1);

            manager.TryResolveThirdPartyCachePath(
                sourcePath,
                typeof(StubPrefabSource),
                cacheVariantKey: null,
                out string cachePath).ShouldBeTrue();
            cachePath.ShouldContain(
                $"{Path.DirectorySeparatorChar}Models{Path.DirectorySeparatorChar}" +
                $"v{ModelBinaryCacheVersions.Schema}{Path.DirectorySeparatorChar}");
            File.Exists(cachePath).ShouldBeFalse(
                "the exclusive model codec must not publish a generic YAML cache while its binary writer is unavailable");

            DateTime futureTimestampUtc = DateTime.UtcNow.AddMinutes(5);
            string legacyCachePath = ResolveLegacyProjectCachePath(
                sandbox,
                sourcePath,
                typeof(StubPrefabSource));
            Directory.CreateDirectory(Path.GetDirectoryName(legacyCachePath)!);
            firstLoad.Payload = "legacy-yaml";
            firstLoad.OriginalLastWriteTimeUtc = futureTimestampUtc;
            firstLoad.SerializeTo(legacyCachePath, AssetManager.Serializer);
            File.SetLastWriteTimeUtc(legacyCachePath, futureTimestampUtc);
            legacyCachePath.ShouldNotBe(cachePath);

            File.WriteAllText(sourcePath, "source-second");
            File.SetLastWriteTimeUtc(sourcePath, futureTimestampUtc.AddMinutes(-1));
            ClearAssetCaches(manager);

            manager.ResolveThirdPartyCacheAuthorityPath<StubPrefabSource>(sourcePath)
                .ShouldBe(Path.GetFullPath(sourcePath));

            StubPrefabSource? secondLoad = manager.Load<StubPrefabSource>(sourcePath);
            secondLoad.ShouldNotBeNull();
            secondLoad.Payload.ShouldBe("source-second");
            StubPrefabSource.LoadCount.ShouldBe(
                2,
                "an exclusive model-cache rejection must return to source import instead of generic YAML deserialization");
            File.Exists(legacyCachePath).ShouldBeTrue(
                "legacy entries remain available for later age-based garbage collection");
            File.Exists(cachePath).ShouldBeFalse(
                "source fallback must not republish the legacy YAML representation");
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

            string[] cacheFiles = WaitForCacheFiles(sandbox.CachePath, 1);
            cacheFiles.Length.ShouldBe(1, "animation clips should now emit a cache asset");
            string cachePath = cacheFiles[0];
            File.Exists(cachePath).ShouldBeTrue();
            byte[] cacheBytes = File.ReadAllBytes(cachePath);
            PublishedCookedAssetRegistry.TryDeserialize(typeof(AnimationClip), cacheBytes, out object? cachedAsset).ShouldBeTrue();
            AnimationClip cachedClipAsset = cachedAsset.ShouldBeOfType<AnimationClip>();
            cachedClipAsset.LengthInSeconds.ShouldBe(clip.LengthInSeconds);

            DateTime cacheTimestampUtc = File.GetLastWriteTimeUtc(cachePath);
            File.WriteAllText(sourcePath, "this is intentionally not valid animation data");
            File.SetLastWriteTimeUtc(sourcePath, cacheTimestampUtc.AddSeconds(-1));

            ClearAssetCaches(manager);

            AnimationClip? cachedClip = manager.Load<AnimationClip>(sourcePath);
            cachedClip.ShouldNotBeNull();
            cachedClip.LengthInSeconds.ShouldBe(1.0f, "fresh cache should satisfy reloads without re-importing the source");
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Test]
    public void LoadAnimationClip_RealSexyWalkClipDoesNotBlockOnCacheWrite()
    {
        using var sandbox = new AssetCacheSandbox();
        var manager = new AssetManager();
        manager.MonitorGameAssetsForChanges = false;
        try
        {
            manager.GameAssetsPath = sandbox.AssetsPath;
            manager.GameCachePath = sandbox.CachePath;

            string sourcePath = Path.Combine(sandbox.AssetsPath, "Walks", "Sexy Walk.anim");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.Copy(Path.Combine(FindRepositoryRoot(), "Assets", "Walks", "Sexy Walk.anim"), sourcePath);

            var stopwatch = Stopwatch.StartNew();
            AnimationClip? clip = manager.Load<AnimationClip>(sourcePath);
            stopwatch.Stop();

            clip.ShouldNotBeNull();
            clip.RootMember.ShouldNotBeNull();
            clip.HasMuscleChannels.ShouldBeTrue();
            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "startup .anim loads must return before cooked cache writes complete");
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

    [Test]
    public void ClearDirty_RemovesTrackedAssetFromDirtyAssets()
    {
        using var sandbox = new AssetCacheSandbox();
        var manager = new AssetManager();
        manager.MonitorGameAssetsForChanges = false;
        try
        {
            manager.GameAssetsPath = sandbox.AssetsPath;
            manager.GameCachePath = sandbox.CachePath;

            StubThirdPartyAsset asset = new() { Name = "Dirty Stub" };
            asset.MarkDirty();

            manager.EnsureTracked(asset);
            manager.DirtyAssets.ContainsKey(asset.ID).ShouldBeTrue();

            asset.ClearDirty();

            asset.IsDirty.ShouldBeFalse();
            manager.DirtyAssets.ContainsKey(asset.ID).ShouldBeFalse();
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

    private static string FindRepositoryRoot()
    {
        string current = Path.GetFullPath(TestContext.CurrentContext.WorkDirectory);
        while (true)
        {
            if (File.Exists(Path.Combine(current, "XRENGINE.sln")))
                return current;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
                throw new DirectoryNotFoundException("Unable to locate repository root containing XRENGINE.sln.");

            current = parent;
        }
    }

    private static string[] WaitForCacheFiles(string cachePath, int expectedCount)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string[] cacheFiles = [];
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            cacheFiles = Directory.Exists(cachePath)
                ? [.. Directory.EnumerateFiles(cachePath, "*", SearchOption.AllDirectories)]
                : [];
            if (cacheFiles.Length >= expectedCount)
                return cacheFiles;

            Thread.Sleep(25);
        }

        return cacheFiles;
    }

    private static string ResolveLegacyProjectCachePath(
        AssetCacheSandbox sandbox,
        string sourcePath,
        Type assetType)
    {
        string relativePath = Path.GetRelativePath(sandbox.AssetsPath, sourcePath);
        string? relativeDirectory = Path.GetDirectoryName(relativePath);
        string cacheDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
            ? sandbox.CachePath
            : Path.Combine(sandbox.CachePath, relativeDirectory);
        string typeSuffix = assetType.FullName ?? assetType.Name;
        return Path.Combine(
            cacheDirectory,
            $"{Path.GetFileName(sourcePath)}.{typeSuffix}.asset");
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

    [XR3rdPartyExtensions("modelstub")]
    private sealed class StubPrefabSource : XRPrefabSource
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
