using System.Numerics;
using System.Security.Cryptography;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Importers.Poiyomi;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
[NonParallelizable]
public sealed class UnityPrefabAvatarImportTests
{
    private IRuntimeShaderServices? _previousShaderServices;
    private IRuntimeRenderingHostServices? _previousRenderingServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        _previousRenderingServices = RuntimeRenderingHostServices.Current;
        RuntimeShaderServices.Current = new UnityAvatarImportTestShaderServices();
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousShaderServices;
        RuntimeRenderingHostServices.Current = _previousRenderingServices!;
    }

    [Test]
    public void SanitizedFixture_ComposesModelProxiesNestedPrefabOverridesAndManifest()
    {
        string prefabPath = ResolveFixturePath("Assets", "SyntheticAvatar.prefab");
        string sourceHash = ComputeSha256(prefabPath);
        DateTime sourceWriteTime = File.GetLastWriteTimeUtc(prefabPath);

        UnityPrefabConversionResult conversion = UnitySceneImporter.ImportPrefabWithManifest(prefabPath);

        SceneNode root = conversion.RootNode.ShouldNotBeNull();
        root.Name.ShouldBe("Sanitized Avatar");
        Transform rootTransform = root.Transform.ShouldBeOfType<Transform>();
        rootTransform.Translation.X.ShouldBe(1.5f, 0.0001f);

        SceneNode[] nodes = EnumerateNodes(root).ToArray();
        nodes.Select(static node => node.Name).ShouldNotContain("GameObject 0");
        nodes.Select(static node => node.Name).ShouldContain("MeshNode");
        SceneNode nested = nodes.Single(static node => node.Name == "Nested Face Tracking Metadata");
        nested.Parent.ShouldBeSameAs(root);

        ModelComponent model = nodes
            .SelectMany(static node => node.Components)
            .OfType<ModelComponent>()
            .Single();
        model.SceneNode.ShouldNotBeNull().Name.ShouldBe("MeshNode");
        model.MeshCastsShadows.ShouldBe(false);
        model.Meshes.Single().RenderInfo.ReceivesShadows.ShouldBeFalse();
        XRMaterial material = model.Model.ShouldNotBeNull()
            .Meshes.Single()
            .LODs.Single()
            .Material.ShouldNotBeNull();
        material.Name.ShouldBe("Synthetic Locked Pro Downgrade");
        material.Parameter<ShaderFloat>("_GrabPass").ShouldBeNull();
        material.Parameter<ShaderFloat>("_RefractionEnabled").ShouldBeNull();

        MaterialConversionReportRegistry.Instance
            .TryGet(material, out MaterialConversionReport? registered)
            .ShouldBeTrue();
        registered.ShouldNotBeNull();
        MaterialConversionReport report = registered!;
        report.Outcome.ShouldBe(EMaterialConversionOutcome.DowngradedToPoiyomiToon);
        report.DiagnosticGroups
            .SelectMany(static group => group.Diagnostics)
            .ShouldContain(static diagnostic =>
                diagnostic.Code == MaterialConversionDiagnosticCodes.ProFeatureDiscarded);

        UnityAvatarDescriptorComponent descriptor = nodes
            .SelectMany(static node => node.Components)
            .OfType<UnityAvatarDescriptorComponent>()
            .Single();
        descriptor.AvatarRoot.ShouldBeSameAs(root.Transform);
        descriptor.VisemeRenderer.ShouldBeSameAs(model);
        descriptor.VisemeBlendShapeNames.ShouldBe(["sil", "aa", "oh"]);
        descriptor.ViewPosition.ShouldBe(new Vector3(0.0f, 1.6f, -0.1f));

        conversion.Manifest.ShouldNotBeNull();
        UnityPrefabImportManifest manifest = conversion.Manifest!;
        manifest.UnityEditorVersion.ShouldBe("2022.3.22f1");
        manifest.CompletionTier.ShouldBe(UnityImportCompletionTier.VisualAndAvatarBehavior);
        manifest.Dependencies.Count(static dependency =>
                Path.GetExtension(dependency.NormalizedPath)
                    .Equals(".mat", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(2);
        manifest.Dependencies.Count(static dependency =>
                dependency.Outcome == UnityImportConversionOutcome.Downgraded)
            .ShouldBe(1);
        manifest.Dependencies.ShouldContain(static dependency =>
            dependency.NormalizedPath.StartsWith("missing://eeee", StringComparison.OrdinalIgnoreCase) &&
            dependency.Kind == UnityImportDependencyKind.AvatarBehavior &&
            dependency.Outcome == UnityImportConversionOutcome.Missing);
        manifest.Diagnostics.ShouldContain(static diagnostic =>
            diagnostic.Code == "UNITYVRC0006" &&
            diagnostic.Severity == UnityImportDiagnosticSeverity.Info);
        manifest.Diagnostics.ShouldNotContain(static diagnostic =>
            diagnostic.Severity == UnityImportDiagnosticSeverity.Error);

        ComputeSha256(prefabPath).ShouldBe(sourceHash);
        File.GetLastWriteTimeUtc(prefabPath).ShouldBe(sourceWriteTime);
    }

    [Test]
    public void ModelImporterMetadata_ParsesGenerationSettingsAndExternalMaterialIdentity()
    {
        string modelPath = ResolveFixturePath("Assets", "Models", "SyntheticAvatar.fbx");

        UnityModelImporterDocument metadata =
            UnityModelImporterDocumentParser.ParseForModel(modelPath);

        metadata.FileIdsGeneration.ShouldBe(2);
        metadata.ImportBlendShapes.ShouldBeTrue();
        metadata.ImportAnimation.ShouldBeTrue();
        metadata.AnimationType.ShouldBe(3);
        metadata.GlobalScale.ShouldBe(1.0f);
        metadata.BakeAxisConversion.ShouldBeFalse();
        metadata.PreserveHierarchy.ShouldBeFalse();
        UnityExternalMaterialRemap remap = metadata.ExternalMaterialRemaps.Single();
        remap.SourceMaterialName.ShouldBe("Stone");
        remap.TargetMaterial.Guid.ShouldBe("22222222222222222222222222222222");
        remap.TargetMaterial.FileId.ShouldBe(2100000);
    }

    [Test]
    public void ModelFileIds_GenerationTwoMatchesUnityAndDisambiguatesDuplicateNames()
    {
        UnityModelFileId.ForGameObject("//RootNode/root")
            .ShouldBe(919132149155446097L);
        UnityModelFileId.ForTransform("//RootNode/root")
            .ShouldBe(-8679921383154817045L);
        UnityModelFileId.ForComponent("MeshRenderer", "//RootNode/root/MeshNode")
            .ShouldBe(-1659616240894028630L);

        long firstDuplicate = UnityModelFileId.ForTransform("//RootNode/root/Bone");
        long secondDuplicate = UnityModelFileId.ForTransform("//RootNode/root/Bone1");
        firstDuplicate.ShouldNotBe(secondDuplicate);
        UnityModelFileId.ForTransform("//RootNode/root/Bone").ShouldBe(firstDuplicate);
    }

    [Test]
    public void ExternalImport_WritesStableOwnedClosureAndRollsBackFailedReimport()
    {
        using var sourceSandbox = new UnityProjectTestSandbox();
        using var outputSandbox = new UnityProjectTestSandbox();
        CopyDirectory(ResolveFixturePath(), sourceSandbox.RootPath);
        string sourcePrefab = Path.Combine(
            sourceSandbox.AssetsPath,
            "SyntheticAvatar.prefab");
        string destination = Path.Combine(
            outputSandbox.AssetsPath,
            "Imported",
            "SyntheticAvatar.asset");
        var options = new ModelImportOptions
        {
            UnityProjectRootOverride = sourceSandbox.RootPath,
            ProcessMeshesAsynchronously = false,
            GenerateMeshRenderersAsync = false,
        };
        var manager = new AssetManager
        {
            MonitorGameAssetsForChanges = false,
            GameAssetsPath = outputSandbox.AssetsPath,
            GameCachePath = outputSandbox.CachePath,
        };
        try
        {
            manager.ImportExternalThirdPartyFile(
                    sourcePrefab,
                    destination,
                    options,
                    overwrite: true)
                .ShouldBeTrue();
            File.Exists(destination).ShouldBeTrue();

            XRPrefabSource imported = manager.Load<XRPrefabSource>(destination).ShouldNotBeNull();
            UnityPrefabImportManifest manifest = imported.UnityImportManifest.ShouldNotBeNull();
            manifest.EntrySourcePath.ShouldBe(Path.GetFullPath(sourcePrefab));
            manifest.OutputAssetPath.ShouldBe(Path.GetFullPath(destination));
            manifest.OwnedOutputPaths.Count.ShouldBeGreaterThan(1);
            manifest.OwnedOutputPaths.ShouldAllBe(static path => File.Exists(path));
            manifest.HasDependencyChanges().ShouldBeFalse();
            string[] firstClosurePaths =
            [
                .. manifest.OwnedOutputPaths
                    .Select(Path.GetFullPath)
                    .Order(StringComparer.OrdinalIgnoreCase),
            ];

            sourceSandbox.WriteAssetWithMeta(
                "Assets/Unrelated.asset",
                "77777777777777777777777777777777",
                "unrelated");
            manifest.HasDependencyChanges().ShouldBeFalse();

            File.AppendAllText(
                Path.Combine(sourceSandbox.AssetsPath, "Materials", "FbxRemap.mat"),
                "\n# reached dependency changed\n");
            manifest.HasDependencyChanges().ShouldBeTrue();
            manager.ImportExternalThirdPartyFile(
                    sourcePrefab,
                    destination,
                    options,
                    overwrite: true)
                .ShouldBeTrue();
            string[] secondClosurePaths =
            [
                .. Directory.EnumerateFiles(
                        Path.Combine(
                            Path.GetDirectoryName(destination)!,
                            Path.GetFileNameWithoutExtension(destination)),
                        "*.asset",
                        SearchOption.AllDirectories)
                    .Append(destination)
                    .Select(Path.GetFullPath)
                    .Order(StringComparer.OrdinalIgnoreCase),
            ];
            secondClosurePaths.ShouldBe(firstClosurePaths);

            Dictionary<string, string> validClosure = secondClosurePaths.ToDictionary(
                static path => path,
                ComputeSha256,
                StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(
                sourcePrefab,
                """
                %YAML 1.1
                --- !u!23 &1
                MeshRenderer:
                  m_Materials:
                  - {fileID: 2100000, guid: ffffffffffffffffffffffffffffffff, type: 2}
                """);

            try
            {
                manager.ImportExternalThirdPartyFile(
                        sourcePrefab,
                        destination,
                        options,
                        overwrite: true)
                    .ShouldBeFalse();
            }
            catch (UnityVisualImportException)
            {
                // The transactional path may surface the classified required-visual failure.
            }

            foreach ((string path, string hash) in validClosure)
            {
                File.Exists(path).ShouldBeTrue(path);
                ComputeSha256(path).ShouldBe(hash, path);
            }
        }
        finally
        {
            manager.Dispose();
        }
    }

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

    private static string ResolveFixturePath(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string root = Path.Combine(
                directory.FullName,
                "XREngine.UnitTests",
                "TestData",
                "UnityAvatarProject");
            string candidate = segments.Length == 0
                ? root
                : Path.Combine(root, Path.Combine(segments));
            if (segments.Length == 0 ? Directory.Exists(candidate) : File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate the synthetic Unity avatar fixture '{Path.Combine(segments)}'.");
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string directory in Directory.EnumerateDirectories(
            sourceDirectory,
            "*",
            SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(
            sourceDirectory,
            "*",
            SearchOption.AllDirectories))
        {
            string destination = Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
