using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Prefabs;

namespace XREngine.UnitTests.Scene;

[TestFixture]
[NonParallelizable]
public sealed class UnityPrefabAvatarPrivateIntegrationTests
{
    private const string DefaultPrivateFixturePath =
        @"K:\Unity\Jax Main Avatars\Assets\Avatars\JAX\Mine\jax2026.prefab";

    private IRuntimeShaderServices? _previousShaderServices;
    private IRuntimeRenderingHostServices? _previousRenderingServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        _previousRenderingServices = RuntimeRenderingHostServices.Current;
        RuntimeShaderServices.Current = new FileBackedRuntimeShaderServices();
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousShaderServices;
        RuntimeRenderingHostServices.Current = _previousRenderingServices!;
    }

    [Test]
    [Category("PrivateIntegration")]
    public void Jax2026_ImportsCompleteVisualAndAvatarClosureWithoutChangingSource()
    {
        string fixturePath = Environment.GetEnvironmentVariable("XRE_UNITY_AVATAR_FIXTURE")
            ?? DefaultPrivateFixturePath;
        if (!File.Exists(fixturePath))
        {
            Assert.Ignore(
                "Private Unity avatar corpus is unavailable. Set XRE_UNITY_AVATAR_FIXTURE to jax2026.prefab to run this opt-in integration test.");
        }

        FileInfo beforeInfo = new(fixturePath);
        string beforeHash = ComputeSha256(fixturePath);
        DateTime beforeWriteTime = beforeInfo.LastWriteTimeUtc;
        long beforeLength = beforeInfo.Length;

        UnityPrefabConversionResult conversion = UnitySceneImporter.ImportPrefabWithManifest(fixturePath);

        SceneNode root = conversion.RootNode.ShouldNotBeNull();
        UnityPrefabImportManifest manifest = conversion.Manifest.ShouldNotBeNull();
        SceneNode[] nodes = EnumerateNodes(root).ToArray();
        XRComponent[] components = nodes.SelectMany(static node => node.Components).ToArray();

        nodes.Length.ShouldBe(883);
        components.OfType<ModelComponent>().Count().ShouldBe(52);
        components.OfType<ModelComponent>()
            .SelectMany(static model => model.DefaultBlendShapeWeights)
            .Count()
            .ShouldBe(6);
        components.OfType<PhysicsChainColliderBase>().Count().ShouldBe(21);
        components.OfType<PhysicsChainComponent>().Count().ShouldBe(14);
        UnityTransformConstraintComponent[] constraints =
            components.OfType<UnityTransformConstraintComponent>().ToArray();
        constraints.Length.ShouldBe(3);
        UnityTransformConstraintComponent[] directConstraints = constraints
            .Where(static constraint =>
                constraint.SceneNode?.Name is "Boob_L" or "Boob_R")
            .ToArray();
        directConstraints.Length.ShouldBe(2);
        foreach (UnityTransformConstraintComponent constraint in directConstraints)
        {
            constraint.TargetTransform.ShouldNotBeNull().SceneNode.ShouldNotBeNull().Name
                .ShouldBe(constraint.SceneNode.ShouldNotBeNull().Name);
            constraint.Sources.Count.ShouldBe(1);
        }
        components.OfType<UnityAvatarDescriptorComponent>().Count().ShouldBe(1);
        components.OfType<UnityAnimatorImportMetadataComponent>().Count().ShouldBeGreaterThanOrEqualTo(1);

        manifest.CompletionTier.ShouldBe(UnityImportCompletionTier.VisualAndAvatarBehavior);
        const string nestedFaceTrackingPackage = "Packages/adjerry91.vrcft.templates/";
        UnityImportDependencyManifestEntry[] mainMaterials = manifest.Dependencies
            .Where(static dependency =>
                dependency.Kind == UnityImportDependencyKind.RequiredVisual &&
                string.Equals(
                    Path.GetExtension(dependency.NormalizedPath),
                    ".mat",
                    StringComparison.OrdinalIgnoreCase) &&
                !dependency.NormalizedPath.StartsWith(
                    nestedFaceTrackingPackage,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        mainMaterials.Length.ShouldBe(86);

        UnityImportDependencyManifestEntry[] mainTextures = manifest.Dependencies
            .Where(static dependency =>
                dependency.Kind == UnityImportDependencyKind.RequiredVisual &&
                IsTextureDependency(dependency.NormalizedPath) &&
                !dependency.NormalizedPath.StartsWith(
                    nestedFaceTrackingPackage,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        mainTextures.Length.ShouldBe(128);
        CountExtension(mainTextures, ".png").ShouldBe(117);
        (CountExtension(mainTextures, ".jpg") + CountExtension(mainTextures, ".jpeg"))
            .ShouldBe(4);
        CountExtension(mainTextures, ".bmp").ShouldBe(2);
        CountExtension(mainTextures, ".exr").ShouldBe(2);
        CountExtension(mainTextures, ".gif").ShouldBe(1);
        CountExtension(mainTextures, ".psd").ShouldBe(1);
        CountExtension(mainTextures, ".hdr").ShouldBe(1);

        HashSet<string> directOverrideMaterialGuids =
        [
            .. Regex.Matches(
                    File.ReadAllText(fixturePath),
                    @"propertyPath:\s*m_Materials\.Array\.data\[\d+\]\s*\r?\n\s*value:[^\r\n]*\r?\n\s*objectReference:\s*\{fileID:\s*2100000,\s*guid:\s*(?<guid>[0-9a-fA-F]{32})",
                    RegexOptions.CultureInvariant)
                .Select(static match => match.Groups["guid"].Value),
        ];
        directOverrideMaterialGuids.Count.ShouldBe(31);
        foreach (string materialGuid in directOverrideMaterialGuids)
        {
            manifest.Dependencies.ShouldContain(dependency =>
                string.Equals(dependency.SourceGuid, materialGuid, StringComparison.OrdinalIgnoreCase) &&
                dependency.Outcome == UnityImportConversionOutcome.Downgraded);
        }
        manifest.Diagnostics.ShouldNotContain(static diagnostic =>
            diagnostic.Severity == UnityImportDiagnosticSeverity.Error);

        var descriptor = components.OfType<UnityAvatarDescriptorComponent>().Single();
        descriptor.AvatarRoot.ShouldNotBeNull();
        descriptor.EyeLook.Enabled.ShouldBeTrue();
        descriptor.EyeLook.LeftEye.ShouldNotBeNull();
        descriptor.EyeLook.RightEye.ShouldNotBeNull();
        descriptor.VisemeRenderer.ShouldNotBeNull();
        descriptor.VisemeBlendShapeNames.Count.ShouldBe(15);

        var afterInfo = new FileInfo(fixturePath);
        afterInfo.Length.ShouldBe(beforeLength);
        afterInfo.LastWriteTimeUtc.ShouldBe(beforeWriteTime);
        ComputeSha256(fixturePath).ShouldBe(beforeHash);
    }

    [Test]
    [Category("PrivateIntegration")]
    public void Jax2026_ExternalizedNativePrefabReloads()
    {
        string? nativeAssetPath =
            Environment.GetEnvironmentVariable("XRE_UNITY_AVATAR_NATIVE_ASSET");
        if (string.IsNullOrWhiteSpace(nativeAssetPath) || !File.Exists(nativeAssetPath))
        {
            Assert.Ignore(
                "Externalized private Unity avatar output is unavailable. Set XRE_UNITY_AVATAR_NATIVE_ASSET to the generated jax2026.asset to run this opt-in integration test.");
        }

        string gameAssetsPath = Directory.GetParent(
            Path.GetDirectoryName(Path.GetFullPath(nativeAssetPath))!)!.FullName;
        var manager = new AssetManager
        {
            MonitorGameAssetsForChanges = false,
            GameAssetsPath = gameAssetsPath,
            GameCachePath = Path.Combine(gameAssetsPath, ".test-cache"),
        };
        try
        {
            XRPrefabSource prefab =
                manager.LoadImmediate<XRPrefabSource>(nativeAssetPath).ShouldNotBeNull();
            prefab.RootNode.ShouldNotBeNull();
            prefab.UnityImportManifest.ShouldNotBeNull();
            EnumerateNodes(prefab.RootNode).Count().ShouldBe(883);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Test]
    [Category("PrivateIntegration")]
    public void Jax2026_ImportedUberMaterialVariantsCompile()
    {
        string fixturePath = Environment.GetEnvironmentVariable("XRE_UNITY_AVATAR_FIXTURE")
            ?? DefaultPrivateFixturePath;
        if (!File.Exists(fixturePath))
        {
            Assert.Ignore(
                "Private Unity avatar corpus is unavailable. Set XRE_UNITY_AVATAR_FIXTURE to jax2026.prefab to run this opt-in integration test.");
        }

        UnityPrefabConversionResult conversion = UnitySceneImporter.ImportPrefabWithManifest(fixturePath);
        SceneNode root = conversion.RootNode.ShouldNotBeNull();
        XRMaterial[] materials = EnumerateNodes(root)
            .SelectMany(static node => node.GetComponents<ModelComponent>())
            .SelectMany(static component => component.Model?.Meshes ?? [])
            .SelectMany(static mesh => mesh.LODs)
            .Select(static lod => lod.Material)
            .Where(static material => material is not null)
            .Cast<XRMaterial>()
            .DistinctBy(static material => material.ID)
            .OrderBy(static material => material.Name, StringComparer.Ordinal)
            .ToArray();

        List<string> failures = [];
        int compiledVariantCount = 0;
        foreach (XRMaterial material in materials)
        {
            if (!material.PrepareUberVariantImmediately())
                continue;

            compiledVariantCount++;
            try
            {
                XRShader fragment = material.GetShader(EShaderType.Fragment).ShouldNotBeNull();
                byte[] spirv = VulkanShaderCompiler.Compile(
                    fragment,
                    out string entryPoint,
                    out _,
                    out string? rewritten);
                spirv.Length.ShouldBeGreaterThan(20);
                entryPoint.ShouldBe("main");
                rewritten.ShouldNotBeNullOrWhiteSpace();
            }
            catch (Exception exception)
            {
                failures.Add(
                    $"{material.Name ?? "<unnamed>"}: {exception.GetBaseException().Message}");
            }
        }

        compiledVariantCount.ShouldBeGreaterThan(0);
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    private static int CountExtension(
        IEnumerable<UnityImportDependencyManifestEntry> dependencies,
        string extension)
        => dependencies.Count(dependency =>
            string.Equals(
                Path.GetExtension(dependency.NormalizedPath),
                extension,
                StringComparison.OrdinalIgnoreCase));

    private static bool IsTextureDependency(string path)
        => Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".exr" or ".gif" or ".psd" or ".hdr";

    private static IEnumerable<SceneNode> EnumerateNodes(SceneNode root)
    {
        var stack = new Stack<SceneNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            SceneNode node = stack.Pop();
            yield return node;
            foreach (SceneNode child in node.Transform.Children
                .Select(static transform => transform.SceneNode)
                .Where(static child => child is not null)
                .Cast<SceneNode>())
            {
                stack.Push(child);
            }
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class FileBackedRuntimeShaderServices : IRuntimeShaderServices
    {
        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
            => new T();

        public T LoadEngineAsset<T>(
            JobPriority priority,
            bool bypassJobThread,
            string assetRoot,
            string relativePath)
            where T : XRAsset, new()
            => CreateEngineAsset<T>(relativePath);

        public Task<T> LoadEngineAssetAsync<T>(
            JobPriority priority,
            bool bypassJobThread,
            string assetRoot,
            string relativePath)
            where T : XRAsset, new()
            => Task.FromResult(CreateEngineAsset<T>(relativePath));

        public void LogWarning(string message)
        {
        }

        private static T CreateEngineAsset<T>(string relativePath) where T : XRAsset, new()
        {
            if (typeof(T) != typeof(XRShader))
                return new T();

            string fullPath = ResolveWorkspacePath(
                Path.Combine("Build", "CommonAssets", "Shaders", relativePath));
            TextFile source = new(fullPath)
            {
                Text = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "void main() {}\n",
            };
            XRShader shader = new(XRShader.ResolveType(Path.GetExtension(relativePath)), source)
            {
                FilePath = fullPath,
                Name = Path.GetFileName(relativePath),
            };
            return (T)(XRAsset)shader;
        }

        private static string ResolveWorkspacePath(string relativePath)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                string candidate = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }

            return relativePath;
        }
    }
}
