using System.Numerics;
using System.Security.Cryptography;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Editor.Importers.SerializedAssets;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Importers.SourceToon;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
[NonParallelizable]
public sealed class SerializedPrefabAvatarImportTests
{
    private IRuntimeShaderServices? _previousShaderServices;
    private IRuntimeRenderingHostServices? _previousRenderingServices;
    private IDisposable? _sourceImportRegistration;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        _previousRenderingServices = RuntimeRenderingHostServices.Current;
        RuntimeShaderServices.Current = new ImportedAvatarImportTestShaderServices();
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
        _sourceImportRegistration = EditorSerializedPrefabImportRegistration.Install();
    }

    [TearDown]
    public void TearDown()
    {
        _sourceImportRegistration?.Dispose();
        _sourceImportRegistration = null;
        RuntimeShaderServices.Current = _previousShaderServices;
        RuntimeRenderingHostServices.Current = _previousRenderingServices!;
    }

    [Test]
    public void SanitizedFixture_ComposesModelProxiesNestedPrefabOverridesAndManifest()
    {
        string prefabPath = ResolveFixturePath("Assets", "SyntheticAvatar.prefab");
        string sourceHash = ComputeSha256(prefabPath);
        DateTime sourceWriteTime = File.GetLastWriteTimeUtc(prefabPath);

        SerializedPrefabConversionResult conversion = SerializedSceneImporter.ImportPrefabWithManifest(prefabPath);

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
        SubMesh subMesh = model.Model.ShouldNotBeNull().Meshes.Single();
        subMesh.RootTransform.ShouldBeSameAs(root.Transform);
        XRMaterial material = subMesh
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
        report.Outcome.ShouldBe(EMaterialConversionOutcome.ConvertedToSourceToon);
        report.DiagnosticGroups
            .SelectMany(static group => group.Diagnostics)
            .ShouldContain(static diagnostic =>
                diagnostic.Code == MaterialConversionDiagnosticCodes.ProFeatureDiscarded);

        AvatarPresentationComponent descriptor = nodes
            .SelectMany(static node => node.Components)
            .OfType<AvatarPresentationComponent>()
            .Single();
        descriptor.AvatarRoot.ShouldBeSameAs(root.Transform);
        descriptor.VisemeRenderer.ShouldBeSameAs(model);
        descriptor.VisemeBlendShapeNames.ShouldBe(["sil", "aa", "oh"]);
        descriptor.ViewPosition.ShouldBe(new Vector3(0.0f, 1.6f, -0.1f));

        conversion.Manifest.ShouldNotBeNull();
        SerializedPrefabImportManifest manifest = conversion.Manifest!;
        manifest.SourceEditorVersion.ShouldBe("2022.3.22f1");
        manifest.CompletionTier.ShouldBe(SourceImportCompletionTier.VisualAndAvatarBehavior);
        manifest.Dependencies.Count(static dependency =>
                Path.GetExtension(dependency.NormalizedPath)
                    .Equals(".mat", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(2);
        manifest.Dependencies.Count(static dependency =>
                dependency.Outcome == SourceImportConversionOutcome.Downgraded)
            .ShouldBe(1);
        manifest.Dependencies.ShouldContain(static dependency =>
            dependency.NormalizedPath.StartsWith("missing://eeee", StringComparison.OrdinalIgnoreCase) &&
            dependency.Kind == SourceImportDependencyKind.AvatarBehavior &&
            dependency.Outcome == SourceImportConversionOutcome.Missing);
        manifest.Diagnostics.ShouldContain(static diagnostic =>
            diagnostic.Code == "UNITYVRC0006" &&
            diagnostic.Severity == SourceImportDiagnosticSeverity.Info);
        manifest.Diagnostics.ShouldNotContain(static diagnostic =>
            diagnostic.Severity == SourceImportDiagnosticSeverity.Error);

        ComputeSha256(prefabPath).ShouldBe(sourceHash);
        File.GetLastWriteTimeUtc(prefabPath).ShouldBe(sourceWriteTime);
    }

    [Test]
    public void ModelImporterMetadata_ParsesGenerationSettingsAndExternalMaterialIdentity()
    {
        string modelPath = ResolveFixturePath("Assets", "Models", "SyntheticAvatar.fbx");

        SerializedModelImporterDocument metadata =
            SerializedModelImporterDocumentParser.ParseForModel(modelPath);

        metadata.FileIdsGeneration.ShouldBe(2);
        metadata.ImportBlendShapes.ShouldBeTrue();
        metadata.ImportAnimation.ShouldBeTrue();
        metadata.AnimationType.ShouldBe(3);
        metadata.GlobalScale.ShouldBe(1.0f);
        metadata.BakeAxisConversion.ShouldBeFalse();
        metadata.PreserveHierarchy.ShouldBeFalse();
        SourceExternalMaterialRemap remap = metadata.ExternalMaterialRemaps.Single();
        remap.SourceMaterialName.ShouldBe("Stone");
        remap.TargetMaterial.Guid.ShouldBe("22222222222222222222222222222222");
        remap.TargetMaterial.FileId.ShouldBe(2100000);
    }

    [Test]
    public void ModelFileIds_GenerationTwoMatchesSourceAndDisambiguatesDuplicateNames()
    {
        SerializedModelFileId.ForGameObject("//RootNode/root")
            .ShouldBe(919132149155446097L);
        SerializedModelFileId.ForTransform("//RootNode/root")
            .ShouldBe(-8679921383154817045L);
        SerializedModelFileId.ForComponent("MeshRenderer", "//RootNode/root/MeshNode")
            .ShouldBe(-1659616240894028630L);

        const string firstDuplicatePath = "//RootNode/root/Bone";
        SerializedModelFileId.ForGameObject(firstDuplicatePath)
            .ShouldBe(-7579103995338469470L);
        SerializedModelFileId.ForTransform(firstDuplicatePath)
            .ShouldBe(3196138216412401344L);
        SerializedModelFileId.ForComponent("MeshFilter", firstDuplicatePath)
            .ShouldBe(-3059082876634828310L);
        SerializedModelFileId.ForComponent("MeshRenderer", firstDuplicatePath)
            .ShouldBe(6384671596192732850L);

        const string secondDuplicatePath = "//RootNode/root/Bone 1";
        SerializedModelFileId.ForGameObject(secondDuplicatePath)
            .ShouldBe(-4808815610696325722L);
        SerializedModelFileId.ForTransform(secondDuplicatePath)
            .ShouldBe(4351210694208761930L);
        SerializedModelFileId.ForComponent("MeshFilter", secondDuplicatePath)
            .ShouldBe(455129110483282244L);
        SerializedModelFileId.ForComponent("MeshRenderer", secondDuplicatePath)
            .ShouldBe(-651623331918672479L);
    }

    [Test]
    public void DuplicateSiblingExport_UsesSourceNamesAndPersistentGenerationTwoIdentities()
    {
        const string modelGuid = "99999999999999999999999999999991";
        SceneNode root = SerializedSceneImporter.ImportPrefab(
            ResolveFixturePath("Assets", "DuplicateSiblingAvatar.prefab"));

        root.Name.ShouldBe("DuplicateSiblingExport");
        SceneNode[] children =
        [
            .. root.Transform.Children
                .Select(static transform => transform.SceneNode)
                .Where(static node => node is not null)
                .Cast<SceneNode>(),
        ];
        children.Select(static child => child.Name).ShouldBe(["Bone", "Bone 1"]);

        AssertSourceIdentity(
            root,
            modelGuid,
            919132149155446097L,
            -8679921383154817045L);
        AssertSourceIdentity(
            children[0],
            modelGuid,
            -7579103995338469470L,
            3196138216412401344L);
        AssertSourceIdentity(
            children[1],
            modelGuid,
            -4808815610696325722L,
            4351210694208761930L);

        children[0].GetComponent<ModelComponent>().ShouldNotBeNull().ID.ShouldBe(
            new SourceAssetIdentity
            {
                AssetGuid = modelGuid,
                LocalFileId = 6384671596192732850L,
                ObjectKind = SourceAssetObjectKind.Renderer,
            }.ToPersistentID());
        children[1].GetComponent<ModelComponent>().ShouldNotBeNull().ID.ShouldBe(
            new SourceAssetIdentity
            {
                AssetGuid = modelGuid,
                LocalFileId = -651623331918672479L,
                ObjectKind = SourceAssetObjectKind.Renderer,
            }.ToPersistentID());
    }

    [Test]
    public void ExternalImport_WritesStableOwnedClosureAndRollsBackFailedReimport()
    {
        using var sourceSandbox = new SourceProjectTestSandbox();
        using var outputSandbox = new SourceProjectTestSandbox();
        using RendererBackendCatalog rendererBackends = new();
        using IDisposable rendererRegistrations =
            BuiltInRendererBackendModules.RegisterAll(rendererBackends);
        CopyDirectory(ResolveFixturePath(), sourceSandbox.RootPath);
        const string textureGuid = "88888888888888888888888888888888";
        string sourceTexturePath = sourceSandbox.WriteAssetWithMeta(
            "Assets/Textures/SyntheticAlbedo.png",
            textureGuid,
            "deferred texture fixture");
        File.WriteAllBytes(
            sourceTexturePath,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        string lockedMaterialPath = Path.Combine(
            sourceSandbox.AssetsPath,
            "Materials",
            "LockedProSynthetic.mat");
        File.WriteAllText(
            lockedMaterialPath,
            File.ReadAllText(lockedMaterialPath).Replace(
                "m_Texture: {fileID: 0}",
                $"m_Texture: {{fileID: 2800000, guid: {textureGuid}, type: 3}}",
                StringComparison.Ordinal));

        string sourcePrefab = Path.Combine(
            sourceSandbox.AssetsPath,
            "SyntheticAvatar.prefab");
        string destination = Path.Combine(
            outputSandbox.AssetsPath,
            "Imported",
            "SyntheticAvatar.asset");
        var options = new ModelImportOptions
        {
            SourceProjectRootOverride = sourceSandbox.RootPath,
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
            SerializedPrefabImportManifestStore.TryLoadAfterRoot(imported, destination).ShouldBeTrue();
            SerializedPrefabImportManifestStore.TryGet(imported, out SerializedPrefabImportManifest? storedManifest).ShouldBeTrue();
            SerializedPrefabImportManifest manifest = storedManifest.ShouldNotBeNull();
            manifest.EntrySourcePath.ShouldBe(Path.GetFullPath(sourcePrefab));
            manifest.OutputAssetPath.ShouldBe(Path.GetFullPath(destination));
            manifest.OwnedOutputPaths.Count.ShouldBeGreaterThan(1);
            manifest.OwnedOutputPaths.ShouldAllBe(static path => File.Exists(path));
            manifest.HasDependencyChanges().ShouldBeFalse();

            string nativeTexturePath = manifest.OwnedOutputPaths.Single(path =>
                string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(path)),
                    "Textures",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    "SyntheticAlbedo",
                    StringComparison.Ordinal));
            XRTexture2D externalizedTexture = AssetManager
                .DeserializeAssetFile(nativeTexturePath, typeof(XRTexture2D))
                .ShouldBeOfType<XRTexture2D>();
            Path.GetFullPath(externalizedTexture.OriginalPath.ShouldNotBeNull()).ShouldBe(
                Path.GetFullPath(sourceTexturePath),
                StringCompareShould.IgnoreCase);
            Path.GetFullPath(externalizedTexture.FilePath.ShouldNotBeNull()).ShouldBe(
                Path.GetFullPath(nativeTexturePath),
                StringCompareShould.IgnoreCase);

            string[] firstClosurePaths =
            [
                .. manifest.OwnedOutputPaths
                    .Select(Path.GetFullPath)
                    .Order(StringComparer.OrdinalIgnoreCase),
            ];
            string[] reusableSubAssets =
            [
                .. firstClosurePaths.Where(path =>
                    !string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)),
            ];
            DateTime timestampBaseline = DateTime.UtcNow.AddHours(-2);
            var unchangedSnapshots = new Dictionary<string, (string Hash, DateTime LastWriteTimeUtc)>(
                StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < reusableSubAssets.Length; index++)
            {
                string path = reusableSubAssets[index];
                File.SetLastWriteTimeUtc(path, timestampBaseline.AddSeconds(index));
                unchangedSnapshots[path] = (ComputeSha256(path), File.GetLastWriteTimeUtc(path));
            }

            manager.ImportExternalThirdPartyFile(
                    sourcePrefab,
                    destination,
                    options,
                    overwrite: true)
                .ShouldBeTrue();
            foreach ((string path, (string hash, DateTime lastWriteTimeUtc)) in unchangedSnapshots)
            {
                ComputeSha256(path).ShouldBe(hash, path);
                File.GetLastWriteTimeUtc(path).ShouldBe(lastWriteTimeUtc, path);
            }

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
            SerializedPrefabImportManifestStore.TryLoadAfterRoot(imported, destination).ShouldBeTrue();
            SerializedPrefabImportManifestStore.TryGet(imported, out SerializedPrefabImportManifest? updatedManifest).ShouldBeTrue();
            string[] secondClosurePaths =
            [
                .. updatedManifest.ShouldNotBeNull().OwnedOutputPaths
                    .Select(Path.GetFullPath)
                    .Order(StringComparer.OrdinalIgnoreCase),
            ];
            secondClosurePaths.Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase)
                .ShouldBe(firstClosurePaths.Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));

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
            catch (SourceVisualImportException)
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

    [Test]
    public async Task ExternalImport_PreCanceledRequestLeavesExistingDestinationUntouched()
    {
        using var outputSandbox = new SourceProjectTestSandbox();
        string sourcePrefab = ResolveFixturePath("Assets", "SyntheticAvatar.prefab");
        string destination = Path.Combine(
            outputSandbox.AssetsPath,
            "Imported",
            "Canceled.asset");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        const string originalContents = "existing native asset";
        File.WriteAllText(destination, originalContents);

        var manager = new AssetManager
        {
            MonitorGameAssetsForChanges = false,
            GameAssetsPath = outputSandbox.AssetsPath,
            GameCachePath = outputSandbox.CachePath,
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await Should.ThrowAsync<OperationCanceledException>(() =>
                manager.ImportExternalThirdPartyFileAsync(
                    sourcePrefab,
                    destination,
                    new ModelImportOptions
                    {
                        SourceProjectRootOverride = ResolveFixturePath(),
                        ProcessMeshesAsynchronously = false,
                        GenerateMeshRenderersAsync = false,
                    },
                    overwrite: true,
                    bypassJobThread: true,
                    cancellationToken: cancellation.Token));

            File.ReadAllText(destination).ShouldBe(originalContents);
            Directory.GetFiles(
                    Path.GetDirectoryName(destination)!,
                    "*.tmp-*",
                    SearchOption.TopDirectoryOnly)
                .ShouldBeEmpty();
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

    private static void AssertSourceIdentity(
        SceneNode node,
        string assetGuid,
        long gameObjectFileId,
        long transformFileId)
    {
        node.ID.ShouldBe(new SourceAssetIdentity
        {
            AssetGuid = assetGuid,
            LocalFileId = gameObjectFileId,
            ObjectKind = SourceAssetObjectKind.GameObject,
        }.ToPersistentID());
        node.Transform.ID.ShouldBe(new SourceAssetIdentity
        {
            AssetGuid = assetGuid,
            LocalFileId = transformFileId,
            ObjectKind = SourceAssetObjectKind.Transform,
        }.ToPersistentID());
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
