using NUnit.Framework;
using Shouldly;
using XREngine.Scene.Importers;
using XREngine.Scene.Prefabs;

namespace XREngine.UnitTests.Scene;

[TestFixture]
[NonParallelizable]
public sealed class UnityProjectImportContextTests
{
    [Test]
    public void ProjectLocator_FindsNearestAssetsAncestorAndSupportsExplicitCorrection()
    {
        using var sandbox = new UnityProjectTestSandbox();
        string prefabPath = sandbox.WriteAsset("Assets/Avatars/Nested/Avatar.prefab");

        UnityProjectLocation automatic = UnityProjectLocator.Locate(prefabPath);

        automatic.ProjectRoot.ShouldBe(Path.GetFullPath(sandbox.RootPath));
        automatic.AssetsRoot.ShouldBe(Path.GetFullPath(sandbox.AssetsPath));
        automatic.HasProjectVersionFile.ShouldBeTrue();
        automatic.UnityEditorVersion.ShouldBe("2022.3.22f1");

        string externalPath = sandbox.WriteAsset("External/Avatar.prefab");
        UnityProjectLocator.TryLocate(
            externalPath,
            out UnityProjectLocation? missing,
            out string? error).ShouldBeFalse();
        missing.ShouldBeNull();
        error.ShouldNotBeNull();
        error!.ShouldContain("Assets");

        UnityProjectLocation corrected = UnityProjectLocator.Locate(
            externalPath,
            sandbox.AssetsPath);
        corrected.ProjectRoot.ShouldBe(Path.GetFullPath(sandbox.RootPath));
    }

    [Test]
    public void GuidIndex_IndexesAssetsEmbeddedPackagesAndPackageCacheWithStablePrecedence()
    {
        using var sandbox = new UnityProjectTestSandbox();
        const string duplicateGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string embeddedGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string cachedGuid = "cccccccccccccccccccccccccccccccc";
        const string refreshedGuid = "dddddddddddddddddddddddddddddddd";

        string selectedAsset = sandbox.WriteAssetWithMeta(
            "Assets/Materials/Selected.mat",
            duplicateGuid);
        sandbox.WriteAssetWithMeta(
            "Packages/com.embedded/Materials/Duplicate.mat",
            duplicateGuid);
        string embeddedAsset = sandbox.WriteAssetWithMeta(
            "Packages/com.embedded/Runtime/Embedded.asset",
            embeddedGuid);
        string cachedAsset = sandbox.WriteAssetWithMeta(
            "Library/PackageCache/com.example.avatar@1.2.3/Runtime/Cached.asset",
            cachedGuid);
        sandbox.WriteAsset(
            "Packages/manifest.json",
            """
            {
              "dependencies": {
                "com.example.avatar": "1.2.3"
              }
            }
            """);
        sandbox.WriteAsset(
            "Packages/packages-lock.json",
            """
            {
              "dependencies": {
                "com.example.avatar": {
                  "version": "1.2.3"
                }
              }
            }
            """);

        UnityGuidIndex index = UnityGuidIndex.GetOrCreate(sandbox.RootPath);
        try
        {
            UnityGuidResolution duplicate = index.Resolve(duplicateGuid);
            duplicate.IsDuplicate.ShouldBeTrue();
            duplicate.Candidates.Count.ShouldBe(2);
            duplicate.Selected.ShouldNotBeNull().AssetPath.ShouldBe(Path.GetFullPath(selectedAsset));
            index.ResolvePath(embeddedGuid).ShouldBe(Path.GetFullPath(embeddedAsset));
            index.ResolvePath(cachedGuid).ShouldBe(Path.GetFullPath(cachedAsset));
            index.Resolve("ffffffffffffffffffffffffffffffff").Selected.ShouldBeNull();
            index.NormalizePortablePath(cachedAsset)
                .ShouldBe("Packages/com.example.avatar/Runtime/Cached.asset");
            index.Diagnostics.ShouldContain(static diagnostic => diagnostic.Code == "UNITYGUID0002");
            index.ScanCount.ShouldBe(1);

            string refreshedAsset = sandbox.WriteAssetWithMeta(
                "Assets/Runtime/AddedAfterScan.asset",
                refreshedGuid);
            UnityGuidIndex.Refresh(sandbox.RootPath);

            index.ResolvePath(refreshedGuid).ShouldBe(Path.GetFullPath(refreshedAsset));
            index.ScanCount.ShouldBe(2);
        }
        finally
        {
            index.Dispose();
        }
    }

    [Test]
    public void DependencyGraph_RecursesReachedClosureRetainsCyclesAndClassifiesOptionalMissingEdges()
    {
        using var sandbox = new UnityProjectTestSandbox();
        const string entryGuid = "10000000000000000000000000000001";
        const string nestedGuid = "20000000000000000000000000000002";
        const string modelGuid = "30000000000000000000000000000003";
        const string materialGuid = "40000000000000000000000000000004";
        const string textureGuid = "50000000000000000000000000000005";
        const string missingExpressionGuid = "60000000000000000000000000000006";

        string entryPath = sandbox.WriteAssetWithMeta(
            "Assets/Avatar.prefab",
            entryGuid,
            $$"""
            %YAML 1.1
            --- !u!1001 &1
            PrefabInstance:
              m_SourcePrefab: {fileID: 100100000, guid: {{modelGuid}}, type: 3}
              nestedPrefab: {fileID: 100100000, guid: {{nestedGuid}}, type: 3}
              expressionsMenu: {fileID: 11400000, guid: {{missingExpressionGuid}}, type: 2}
            """);
        string nestedPath = sandbox.WriteAssetWithMeta(
            "Assets/Nested.prefab",
            nestedGuid,
            $$"""
            %YAML 1.1
            --- !u!1001 &2
            PrefabInstance:
              m_SourcePrefab: {fileID: 100100000, guid: {{entryGuid}}, type: 3}
            """);
        string modelPath = sandbox.WriteAssetWithMeta(
            "Assets/Models/Avatar.fbx",
            modelGuid,
            "synthetic model payload",
            $$"""
            ModelImporter:
              externalObjects:
              - first:
                  name: Body
                second: {fileID: 2100000, guid: {{materialGuid}}, type: 2}
              meshes:
                fileIdsGeneration: 2
            """);
        string materialPath = sandbox.WriteAssetWithMeta(
            "Assets/Materials/Body.mat",
            materialGuid,
            $$"""
            %YAML 1.1
            --- !u!21 &2100000
            Material:
              m_SavedProperties:
                m_TexEnvs:
                - _MainTex:
                    m_Texture: {fileID: 2800000, guid: {{textureGuid}}, type: 3}
            """);
        string texturePath = sandbox.WriteAssetWithMeta(
            "Assets/Textures/Body.png",
            textureGuid,
            "synthetic texture bytes");

        var context = new UnityProjectImportContext(entryPath);
        UnityDependencyGraph graph = context.DiscoverDependencies();

        graph.Nodes.Keys.ShouldContain(Path.GetFullPath(entryPath));
        graph.Nodes.Keys.ShouldContain(Path.GetFullPath(nestedPath));
        graph.Nodes.Keys.ShouldContain(Path.GetFullPath(modelPath));
        graph.Nodes.Keys.ShouldContain(Path.GetFullPath($"{modelPath}.meta"));
        graph.Nodes.Keys.ShouldContain(Path.GetFullPath(materialPath));
        graph.Nodes.Keys.ShouldContain(Path.GetFullPath(texturePath));

        UnityDependencyEdge modelEdge = graph.Nodes[Path.GetFullPath(entryPath)]
            .OutgoingEdges.Single(edge => edge.TargetGuid == modelGuid);
        modelEdge.TargetFileId.ShouldBe(100100000);
        modelEdge.ReferringProperty.ShouldBe("m_SourcePrefab");
        modelEdge.Kind.ShouldBe(UnityImportDependencyKind.RequiredVisual);

        graph.Nodes[Path.GetFullPath(nestedPath)].OutgoingEdges
            .ShouldContain(static edge => edge.IsCycle);
        graph.UnresolvedEdges.ShouldContain(edge =>
            edge.TargetGuid == missingExpressionGuid &&
            edge.Kind == UnityImportDependencyKind.AvatarBehavior);
        context.Diagnostics.ShouldContain(static diagnostic => diagnostic.Code == "UNITYDEP0003");
        context.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "UNITYDEP0001" &&
            diagnostic.Severity == UnityImportDiagnosticSeverity.Warning);

        UnityPrefabImportManifest manifest = context.CreateManifest(
            UnityImportCompletionTier.VisualAndAvatarBehavior);
        manifest.HasDependencyChanges().ShouldBeFalse();

        sandbox.WriteAssetWithMeta(
            "Assets/Unrelated.asset",
            "70000000000000000000000000000007",
            "unrelated edit");
        manifest.HasDependencyChanges().ShouldBeFalse();

        File.AppendAllText(texturePath, "-changed");
        manifest.GetChangedDependencyPaths().ShouldContain(Path.GetFullPath(texturePath));
    }

    [Test]
    public void DependencyGraph_MissingRequiredVisualFailsBeforeConversion()
    {
        using var sandbox = new UnityProjectTestSandbox();
        string prefabPath = sandbox.WriteAsset(
            "Assets/Broken.prefab",
            """
            %YAML 1.1
            --- !u!23 &1
            MeshRenderer:
              m_Materials:
              - {fileID: 2100000, guid: ffffffffffffffffffffffffffffffff, type: 2}
            """);

        var context = new UnityProjectImportContext(prefabPath);

        Should.Throw<UnityVisualImportException>(() => context.DiscoverDependencies())
            .Message.ShouldContain("required visual");
    }
}
