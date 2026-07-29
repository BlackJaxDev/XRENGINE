using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ModelCachePathResolverTests
{
    [Test]
    public void Resolve_PartitionsProjectEngineAndExternalSources()
    {
        string root = CreateTestRoot();
        string projectRoot = Path.Combine(root, "Project", "Assets");
        string engineRoot = Path.Combine(root, "Engine", "Assets");
        string cacheRoot = Path.Combine(root, "Cache");
        string projectSource = Path.Combine(projectRoot, "Models", "Hero.gltf");
        string engineSource = Path.Combine(engineRoot, "Models", "Grid.gltf");
        string externalSource = Path.Combine(root, "External", "Hero.gltf");

        ModelCachePathResolution project = Resolve(
            projectSource,
            projectRoot,
            engineRoot,
            cacheRoot);
        ModelCachePathResolution engine = Resolve(
            engineSource,
            projectRoot,
            engineRoot,
            cacheRoot);
        ModelCachePathResolution external = Resolve(
            externalSource,
            projectRoot,
            engineRoot,
            cacheRoot);

        project.SourceIdentity.Origin.ShouldBe(ModelCacheSourceOrigin.Project);
        engine.SourceIdentity.Origin.ShouldBe(ModelCacheSourceOrigin.Engine);
        external.SourceIdentity.Origin.ShouldBe(ModelCacheSourceOrigin.External);
        project.CachePath.ShouldContain(
            $"{Path.DirectorySeparatorChar}Project{Path.DirectorySeparatorChar}");
        engine.CachePath.ShouldContain(
            $"{Path.DirectorySeparatorChar}Engine{Path.DirectorySeparatorChar}");
        external.CachePath.ShouldContain(
            $"{Path.DirectorySeparatorChar}External{Path.DirectorySeparatorChar}");
        project.UsedHashedSourcePath.ShouldBeFalse();
        engine.UsedHashedSourcePath.ShouldBeFalse();
        external.UsedHashedSourcePath.ShouldBeTrue();
        project.CachePath.ShouldEndWith(
            Path.Combine("models", "hero.gltf.asset"));
        engine.CachePath.ShouldEndWith(
            Path.Combine("models", "grid.gltf.asset"));
    }

    [Test]
    public void SourceIdentity_UsesInvariantWindowsCasePolicy()
    {
        string root = CreateTestRoot();
        string projectRoot = Path.Combine(root, "Project", "Assets");
        string sourcePath = Path.Combine(projectRoot, "Models", "İstanbul", "Hero.GLTF");

        ModelCacheSourceIdentity mixedCase = ModelCacheSourceIdentityResolver.Resolve(
            sourcePath,
            projectRoot,
            engineAssetsRoot: null);
        ModelCacheSourceIdentity upperCase = ModelCacheSourceIdentityResolver.Resolve(
            sourcePath.ToUpperInvariant(),
            projectRoot.ToUpperInvariant(),
            engineAssetsRoot: null);

        upperCase.CanonicalIdentity.ShouldBe(mixedCase.CanonicalIdentity);
        upperCase.IdentityHash.ShouldBe(mixedCase.IdentityHash);

        string cacheRoot = Path.Combine(root, "Cache");
        Resolve(sourcePath, projectRoot, engineRoot: null, cacheRoot).CachePath.ShouldBe(
            Resolve(
                sourcePath.ToUpperInvariant(),
                projectRoot.ToUpperInvariant(),
                engineRoot: null,
                cacheRoot).CachePath);
    }

    [Test]
    public void Resolve_UsesDeterministicHashedFallbackForLongAndReservedPaths()
    {
        string root = CreateTestRoot();
        string projectRoot = Path.Combine(root, "Assets");
        string cacheRoot = Path.Combine(root, "Cache");
        string longRelativePath = Path.Combine(
            Enumerable.Repeat(new string('a', 40), 5).Append("model.gltf").ToArray());
        string longSource = Path.Combine(projectRoot, longRelativePath);
        string reservedSource = Path.Combine(projectRoot, "Models", "CON.fbx");

        ModelCachePathResolution firstLong = Resolve(
            longSource,
            projectRoot,
            engineRoot: null,
            cacheRoot);
        ModelCachePathResolution secondLong = Resolve(
            longSource,
            projectRoot,
            engineRoot: null,
            cacheRoot);
        ModelCachePathResolution reserved = Resolve(
            reservedSource,
            projectRoot,
            engineRoot: null,
            cacheRoot);

        firstLong.UsedHashedSourcePath.ShouldBeTrue();
        firstLong.CachePath.ShouldBe(secondLong.CachePath);
        firstLong.CachePath.ShouldContain(
            $"{Path.DirectorySeparatorChar}hashed{Path.DirectorySeparatorChar}");
        firstLong.CachePath.ShouldEndWith(".asset");
        reserved.UsedHashedSourcePath.ShouldBeTrue();
        reserved.CachePath.ShouldContain(
            $"{Path.DirectorySeparatorChar}hashed{Path.DirectorySeparatorChar}");
        reserved.CachePath.ShouldNotContain("CON.fbx");
    }

    [Test]
    public void Resolve_ExternalSourcesWithTheSameNameDoNotCollide()
    {
        string root = CreateTestRoot();
        string cacheRoot = Path.Combine(root, "Cache");
        string firstSource = Path.Combine(root, "ExternalA", "shared.gltf");
        string secondSource = Path.Combine(root, "ExternalB", "shared.gltf");

        ModelCachePathResolution first = Resolve(
            firstSource,
            projectRoot: null,
            engineRoot: null,
            cacheRoot);
        ModelCachePathResolution second = Resolve(
            secondSource,
            projectRoot: null,
            engineRoot: null,
            cacheRoot);

        first.SourceIdentity.IdentityHash.ShouldNotBe(second.SourceIdentity.IdentityHash);
        first.CachePath.ShouldNotBe(second.CachePath);
        first.UsedHashedSourcePath.ShouldBeTrue();
        second.UsedHashedSourcePath.ShouldBeTrue();
    }

    [Test]
    public void Resolve_HashesCallerVariantInsteadOfUsingItAsAPathSegment()
    {
        string root = CreateTestRoot();
        string projectRoot = Path.Combine(root, "Assets");
        string cacheRoot = Path.Combine(root, "Cache");
        string sourcePath = Path.Combine(projectRoot, "model.gltf");
        ModelImportOptions options = new();
        ModelImportBackendResolution backendResolution = ModelImportBackendResolver.Resolve(
            sourcePath,
            options);
        ModelCacheVariantFingerprint fingerprint = ModelCacheVariantFingerprintBuilder.Compute(
            sourcePath,
            options,
            backendResolution,
            callerVariantKey: @"..\..\free-form");
        ModelCacheSourceIdentity sourceIdentity = ModelCacheSourceIdentityResolver.Resolve(
            sourcePath,
            projectRoot,
            engineAssetsRoot: null);

        ModelCachePathResolution resolution = ModelCachePathResolver.Resolve(
            cacheRoot,
            sourceIdentity,
            backendResolution,
            fingerprint);

        resolution.CachePath.ShouldStartWith(Path.GetFullPath(cacheRoot));
        resolution.CachePath.ShouldNotContain("free-form");
        resolution.CachePath.ShouldContain($"opts_{fingerprint.Value}");
    }

    private static ModelCachePathResolution Resolve(
        string sourcePath,
        string? projectRoot,
        string? engineRoot,
        string cacheRoot)
    {
        ModelImportOptions options = new();
        ModelImportBackendResolution backendResolution = ModelImportBackendResolver.Resolve(
            sourcePath,
            options);
        ModelCacheVariantFingerprint fingerprint = ModelCacheVariantFingerprintBuilder.Compute(
            sourcePath,
            options,
            backendResolution);
        ModelCacheSourceIdentity sourceIdentity = ModelCacheSourceIdentityResolver.Resolve(
            Path.GetFullPath(sourcePath),
            projectRoot,
            engineRoot);
        return ModelCachePathResolver.Resolve(
            Path.GetFullPath(cacheRoot),
            sourceIdentity,
            backendResolution,
            fingerprint);
    }

    private static string CreateTestRoot()
        => Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "xre-model-cache-paths",
            Guid.NewGuid().ToString("N")));
}
