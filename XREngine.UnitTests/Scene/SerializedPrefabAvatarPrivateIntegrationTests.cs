using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Editor.Importers.SerializedAssets;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Vulkan;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
[NonParallelizable]
public sealed class SerializedPrefabAvatarPrivateIntegrationTests
{
    private const string DefaultPrivateFixturePath =
        @"K:\Unity\Jax Main Avatars\Assets\Avatars\JAX\Mine\jax2026.prefab";

    private IRuntimeShaderServices? _previousShaderServices;
    private IRuntimeRenderingHostServices? _previousRenderingServices;
    private RendererBackendCatalog? _rendererBackendCatalog;
    private IDisposable? _rendererBackendRegistrations;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        _previousRenderingServices = RuntimeRenderingHostServices.Current;
        RuntimeShaderServices.Current = new FileBackedRuntimeShaderServices();
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
        _rendererBackendCatalog = new RendererBackendCatalog();
        _rendererBackendRegistrations =
            BuiltInRendererBackendModules.RegisterAll(_rendererBackendCatalog);
    }

    [TearDown]
    public void TearDown()
    {
        _rendererBackendRegistrations?.Dispose();
        _rendererBackendCatalog?.Dispose();
        _rendererBackendRegistrations = null;
        _rendererBackendCatalog = null;
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

        SerializedPrefabConversionResult conversion = SerializedSceneImporter.ImportPrefabWithManifest(fixturePath);

        SceneNode root = conversion.RootNode.ShouldNotBeNull();
        SerializedPrefabImportManifest manifest = conversion.Manifest.ShouldNotBeNull();
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
        WeightedTransformConstraintComponent[] constraints =
            components.OfType<WeightedTransformConstraintComponent>().ToArray();
        constraints.Length.ShouldBe(3);
        WeightedTransformConstraintComponent[] directConstraints = constraints
            .Where(static constraint =>
                constraint.SceneNode?.Name is "Boob_L" or "Boob_R")
            .ToArray();
        directConstraints.Length.ShouldBe(2);
        foreach (WeightedTransformConstraintComponent constraint in directConstraints)
        {
            constraint.TargetTransform.ShouldNotBeNull().SceneNode.ShouldNotBeNull().Name
                .ShouldBe(constraint.SceneNode.ShouldNotBeNull().Name);
            constraint.Sources.Count.ShouldBe(1);
        }
        components.OfType<AvatarPresentationComponent>().Count().ShouldBe(1);
        manifest.Animators.Count.ShouldBeGreaterThanOrEqualTo(1);

        manifest.CompletionTier.ShouldBe(SourceImportCompletionTier.VisualAndAvatarBehavior);
        const string nestedFaceTrackingPackage = "Packages/adjerry91.vrcft.templates/";
        SourceImportDependencyManifestEntry[] mainMaterials = manifest.Dependencies
            .Where(static dependency =>
                dependency.Kind == SourceImportDependencyKind.RequiredVisual &&
                string.Equals(
                    Path.GetExtension(dependency.NormalizedPath),
                    ".mat",
                    StringComparison.OrdinalIgnoreCase) &&
                !dependency.NormalizedPath.StartsWith(
                    nestedFaceTrackingPackage,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        mainMaterials.Length.ShouldBe(86);

        SourceImportDependencyManifestEntry[] mainTextures = manifest.Dependencies
            .Where(static dependency =>
                dependency.Kind == SourceImportDependencyKind.RequiredVisual &&
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
                dependency.Outcome == SourceImportConversionOutcome.Downgraded);
        }
        manifest.Diagnostics.ShouldNotContain(static diagnostic =>
            diagnostic.Severity == SourceImportDiagnosticSeverity.Error);

        var descriptor = components.OfType<AvatarPresentationComponent>().Single();
        descriptor.AvatarRoot.ShouldNotBeNull();
        descriptor.EyeLook.Enabled.ShouldBeTrue();
        descriptor.EyeLook.LeftEye.ShouldNotBeNull();
        descriptor.EyeLook.RightEye.ShouldNotBeNull();
        descriptor.VisemeRenderer.ShouldNotBeNull();
        descriptor.VisemeBlendShapeNames.Count.ShouldBe(15);

        AssertAuthoredPrefabFidelity(fixturePath, root, manifest);
        AssertCompleteSkinningAndOrientation(
            root,
            components.OfType<ModelComponent>(),
            "Direct Unity conversion");

        var afterInfo = new FileInfo(fixturePath);
        afterInfo.Length.ShouldBe(beforeLength);
        afterInfo.LastWriteTimeUtc.ShouldBe(beforeWriteTime);
        ComputeSha256(fixturePath).ShouldBe(beforeHash);
    }

    [Test]
    [Category("PrivateIntegration")]
    public async Task Jax2026_ExternalizedNativePrefabReloads()
    {
        string? nativeAssetPath =
            Environment.GetEnvironmentVariable("XRE_UNITY_AVATAR_NATIVE_ASSET");
        if (string.IsNullOrWhiteSpace(nativeAssetPath) || !File.Exists(nativeAssetPath))
        {
            Assert.Ignore(
                "Externalized private Unity avatar output is unavailable. Set XRE_UNITY_AVATAR_NATIVE_ASSET to the generated jax2026.asset to run this opt-in integration test.");
        }

        string? nativeMetadataPath =
            Environment.GetEnvironmentVariable("XRE_UNITY_AVATAR_NATIVE_METADATA");
        if (string.IsNullOrWhiteSpace(nativeMetadataPath) ||
            !Directory.Exists(nativeMetadataPath))
        {
            Assert.Ignore(
                "Externalized private Unity avatar metadata is unavailable. Set XRE_UNITY_AVATAR_NATIVE_METADATA to the metadata root paired with XRE_UNITY_AVATAR_NATIVE_ASSET.");
        }

        string gameAssetsPath = Path.GetDirectoryName(
            Path.GetFullPath(nativeAssetPath))!;
        string gameCachePath = Path.Combine(
            Path.GetDirectoryName(gameAssetsPath)!,
            ".test-cache");
        bool previousMonitorSetting = Engine.Assets.MonitorGameAssetsForChanges;
        string previousAssetsPath = Engine.Assets.GameAssetsPath;
        string? previousMetadataPath = Engine.Assets.GameMetadataPath;
        string? previousCachePath = Engine.Assets.GameCachePath;
        try
        {
            Engine.Assets.MonitorGameAssetsForChanges = false;
            Engine.Assets.GameAssetsPath = gameAssetsPath;
            Engine.Assets.GameMetadataPath = nativeMetadataPath;
            Engine.Assets.GameCachePath = gameCachePath;
            ClearAssetCaches();

            XRPrefabSource prefab = (await Engine.Assets
                .LoadPrefabWithReferencesAsync(
                    nativeAssetPath,
                    bypassJobThread: true))
                .ShouldNotBeNull();
            SceneNode templateRoot = prefab.RootNode.ShouldNotBeNull();
            SerializedPrefabImportManifestStore.TryLoadAfterRoot(prefab, nativeAssetPath).ShouldBeTrue();
            SerializedPrefabImportManifestStore.TryGet(prefab, out SerializedPrefabImportManifest? manifest).ShouldBeTrue();
            manifest.ShouldNotBeNull();
            SceneNode[] templateNodes = EnumerateNodes(templateRoot).ToArray();
            templateNodes.Length.ShouldBe(883);
            templateNodes.SelectMany(static node => node.Components)
                .OfType<ModelComponent>()
                .Count()
                .ShouldBe(52);

            Model[] nativeModels =
            [
                .. templateNodes
                    .SelectMany(static node => node.GetComponents<ModelComponent>())
                    .Select(static component => component.Model)
                    .Where(static model => model is not null)
                    .Cast<Model>()
                    .DistinctBy(static model => model.ID),
            ];
            SubMesh[] nativeSubMeshes =
            [
                .. nativeModels
                    .SelectMany(static model => model.Meshes)
                    .DistinctBy(static mesh => mesh.ID),
            ];
            XRMaterial[] nativeMaterials =
            [
                .. nativeSubMeshes
                    .SelectMany(static mesh => mesh.LODs)
                    .Select(static lod => lod.Material)
                    .Where(static material => material is not null)
                    .Cast<XRMaterial>()
                    .DistinctBy(static material => material.ID),
            ];
            XRTexture2D[] nativeTextures =
            [
                .. nativeMaterials
                    .SelectMany(static material => material.Textures)
                    .OfType<XRTexture2D>()
                    .DistinctBy(static texture => texture.ID),
            ];
            XRTexture2D[] importedSourceTextures =
            [
                .. nativeTextures.Where(static texture =>
                    !string.IsNullOrWhiteSpace(texture.OriginalPath) &&
                    !string.Equals(
                        Path.GetExtension(texture.OriginalPath),
                        ".asset",
                        StringComparison.OrdinalIgnoreCase)),
            ];
            TestContext.Progress.WriteLine(
                $"Native jax2026 references {nativeModels.Length} models, {nativeSubMeshes.Length} submeshes, {nativeMaterials.Length} materials, {nativeTextures.Length} total texture resources, and {importedSourceTextures.Length} imported source textures.");
            importedSourceTextures.Length.ShouldBe(
                90,
                $"Native closure loaded {nativeModels.Length} models, {nativeSubMeshes.Length} submeshes, and {nativeMaterials.Length} materials.");
            importedSourceTextures.ShouldAllBe(static texture =>
                !string.IsNullOrWhiteSpace(texture.OriginalPath) &&
                File.Exists(texture.OriginalPath));
            importedSourceTextures.ShouldAllBe(static texture =>
                string.Equals(
                    Path.GetExtension(texture.FilePath),
                    ".asset",
                    StringComparison.OrdinalIgnoreCase));

            nativeMaterials
                .Count(static material => material.TryGetUberMaterialState(out _, out _))
                .ShouldBeGreaterThan(0);

            SceneNode instance = prefab.Instantiate();
            instance.Transform
                .RecalculateMatrixHierarchy(
                    forceWorldRecalc: true,
                    setRenderMatrixNow: true,
                    ELoopType.Sequential)
                .GetAwaiter()
                .GetResult();
            SceneNode[] instanceNodes = EnumerateNodes(instance).ToArray();
            instanceNodes.Length.ShouldBe(883);
            ModelComponent[] activeModelComponents =
            [
                .. instanceNodes
                    .Where(IsEffectivelyActive)
                    .SelectMany(static node => node.GetComponents<ModelComponent>()),
            ];
            TestContext.Progress.WriteLine(
                $"Native jax2026 instance contains {activeModelComponents.Length} effectively active model components.");
            activeModelComponents.Length.ShouldBe(22);
            instanceNodes.Single(static node => node.Name == "Meshes")
                .IsActiveSelf.ShouldBeTrue();
            SceneNode bodyNode = instanceNodes.Single(static node => node.Name == "Body");
            bodyNode.IsActiveSelf.ShouldBeTrue();
            instanceNodes.Single(static node => node.Name == "Face")
                .IsActiveSelf.ShouldBeTrue();

            ModelComponent bodyComponent = bodyNode.GetComponent<ModelComponent>().ShouldNotBeNull();
            XRMesh[] bodyRuntimeMeshes =
            [
                .. bodyComponent.Meshes
                    .SelectMany(static mesh => mesh.GetLodSnapshot())
                    .Select(static lod => lod.Renderer.Mesh)
                    .Where(static mesh => mesh is not null)
                    .Cast<XRMesh>(),
            ];
            bodyRuntimeMeshes.ShouldNotBeEmpty();
            bodyRuntimeMeshes.ShouldAllBe(static mesh => mesh.HasSkinning);
            AssertCompleteSkinningAndOrientation(
                instance,
                instanceNodes.SelectMany(static node => node.GetComponents<ModelComponent>()),
                "Externalized native prefab reload");
        }
        finally
        {
            ClearAssetCaches();
            Engine.Assets.GameAssetsPath = previousAssetsPath;
            Engine.Assets.GameMetadataPath = previousMetadataPath;
            Engine.Assets.GameCachePath = previousCachePath;
            Engine.Assets.MonitorGameAssetsForChanges = previousMonitorSetting;
        }
    }

    private static bool IsSelfOrDescendantOf(TransformBase root, TransformBase candidate)
    {
        for (TransformBase? current = candidate; current is not null; current = current.Parent)
            if (ReferenceEquals(current, root))
                return true;

        return false;
    }

    private static void AssertCompleteSkinningAndOrientation(
        SceneNode avatarRoot,
        IEnumerable<ModelComponent> modelComponents,
        string context)
    {
        avatarRoot.Transform
            .RecalculateMatrixHierarchy(
                forceWorldRecalc: true,
                setRenderMatrixNow: true,
                ELoopType.Sequential)
            .GetAwaiter()
            .GetResult();

        ModelComponent[] components = [.. modelComponents];
        int sourceLodCount = 0;
        int runtimeLodCount = 0;
        List<XRMesh> runtimeMeshes = [];
        foreach (ModelComponent component in components)
        {
            Model model = component.Model.ShouldNotBeNull(
                $"{context}: model component '{component.SceneNode?.Name}' must retain its model asset.");
            component.Meshes.Count.ShouldBe(
                model.Meshes.Count,
                $"{context}: runtime renderer count must match the source submesh count on '{component.SceneNode?.Name}'.");

            sourceLodCount += model.Meshes.Sum(static subMesh =>
                subMesh.LODs.Count(static lod => lod.Mesh is not null));
            foreach (RenderableMesh renderable in component.Meshes)
            {
                RenderableMesh.RenderableLOD[] lods = renderable.GetLodSnapshot();
                runtimeLodCount += lods.Count(static lod => lod.Renderer.Mesh is not null);
                runtimeMeshes.AddRange(
                    lods
                        .Select(static lod => lod.Renderer.Mesh)
                        .Where(static mesh => mesh is not null)
                        .Cast<XRMesh>());
            }
        }

        runtimeLodCount.ShouldBe(
            sourceLodCount,
            $"{context}: every imported model LOD must have a runtime mesh renderer.");
        XRMesh[] distinctMeshes = [.. runtimeMeshes.DistinctBy(static mesh => mesh.ID)];
        XRMesh[] skinnedMeshes = [.. distinctMeshes.Where(static mesh => mesh.HasSkinning)];
        skinnedMeshes.ShouldNotBeEmpty($"{context}: the avatar must contain skinned meshes.");

        (long alignedFaces, long opposedFaces, string worstMesh, float worstAlignedRatio) =
            AuditTriangleWindingAgainstNormals(distinctMeshes);
        TestContext.Progress.WriteLine(
            $"{context} normal/winding audit: alignedFaces={alignedFaces}, opposedFaces={opposedFaces}, " +
            $"worstMesh='{worstMesh}', worstAlignedRatio={worstAlignedRatio:F6}.");

        TransformBase rootTransform = avatarRoot.Transform;
        long totalVertices = 0;
        float maximumBoneIdentityError = 0.0f;
        float maximumVertexBindDisplacement = 0.0f;
        float maximumWeightSumError = 0.0f;
        foreach (XRMesh mesh in skinnedMeshes)
        {
            mesh.UtilizedBones
                .Select(static bone => bone.tfm)
                .ShouldAllBe(
                    bone => IsSelfOrDescendantOf(rootTransform, bone),
                    $"{context}: mesh '{mesh.Name}' must bind only to bones in this avatar instance.");

            SkinningBindPoseAuditResult audit = mesh.CalculateBindPoseAudit();
            totalVertices += audit.VertexCount;
            maximumBoneIdentityError = MathF.Max(
                maximumBoneIdentityError,
                audit.MaximumBoneIdentityError);
            maximumVertexBindDisplacement = MathF.Max(
                maximumVertexBindDisplacement,
                audit.MaximumVertexBindDisplacement);
            maximumWeightSumError = MathF.Max(
                maximumWeightSumError,
                audit.MaximumWeightSumError);

            audit.VertexCount.ShouldBe(mesh.VertexCount);
            audit.NonFiniteVertexCount.ShouldBe(
                0,
                $"{context}: mesh '{mesh.Name}' contains non-finite source or bind-pose positions.");
            audit.NonFiniteMatrixCount.ShouldBe(
                0,
                $"{context}: mesh '{mesh.Name}' contains non-finite bind matrices.");
            audit.InvalidInfluenceCount.ShouldBe(
                0,
                $"{context}: mesh '{mesh.Name}' contains invalid skin weights.");
            audit.MissingPaletteBoneCount.ShouldBe(
                0,
                $"{context}: mesh '{mesh.Name}' has vertex weights that are absent from its runtime palette.");
            audit.UnweightedVertexCount.ShouldBe(
                0,
                $"{context}: every vertex in skinned mesh '{mesh.Name}' must have at least one influence.");
            audit.MaximumWeightSumError.ShouldBeLessThan(
                0.002f,
                $"{context}: mesh '{mesh.Name}' has non-normalized weights at vertex {audit.MaximumWeightSumErrorVertexIndex}.");
            audit.MaximumInfluenceInverseBindDifference.ShouldBeLessThan(
                0.00001f,
                $"{context}: mesh '{mesh.Name}' disagrees between vertex and palette inverse-bind matrices at vertex {audit.MaximumInfluenceInverseBindDifferenceVertexIndex}.");
            audit.MaximumBoneIdentityError.ShouldBeLessThan(
                0.002f,
                $"{context}: mesh '{mesh.Name}' bind palette does not cancel to identity at bone '{audit.MaximumBoneIdentityErrorBoneName ?? "<none>"}'.");
            audit.MaximumVertexBindDisplacement.ShouldBeLessThan(
                0.002f,
                $"{context}: mesh '{mesh.Name}' moves vertex {audit.MaximumVertexBindDisplacementIndex} while reconstructed at bind pose.");
        }

        SceneNode hips = FindNodeByPath(avatarRoot, "Armature", "Hips");
        SceneNode head = FindNodeByPath(avatarRoot, "Armature", "Hips", "Spine", "Chest", "Neck", "Head");
        SceneNode leftFoot = FindNodeByPath(avatarRoot, "Armature", "Hips", "Leg_L", "Knee_L", "Foot_L");
        SceneNode rightFoot = FindNodeByPath(avatarRoot, "Armature", "Hips", "Leg_R", "Knee_R", "Foot_R");
        SceneNode leftToe = FindNodeByPath(avatarRoot, "Armature", "Hips", "Leg_L", "Knee_L", "Foot_L", "Toe_L");
        SceneNode rightToe = FindNodeByPath(avatarRoot, "Armature", "Hips", "Leg_R", "Knee_R", "Foot_R", "Toe_R");

        Vector3 hipsPosition = hips.Transform.BindMatrix.Translation;
        Vector3 headPosition = head.Transform.BindMatrix.Translation;
        Vector3 feetPosition = (leftFoot.Transform.BindMatrix.Translation + rightFoot.Transform.BindMatrix.Translation) * 0.5f;
        Vector3 toesPosition = (leftToe.Transform.BindMatrix.Translation + rightToe.Transform.BindMatrix.Translation) * 0.5f;
        headPosition.Y.ShouldBeGreaterThan(
            hipsPosition.Y + 0.25f,
            $"{context}: the converted avatar must be upright with its head above its hips.");
        feetPosition.Y.ShouldBeLessThan(
            hipsPosition.Y - 0.25f,
            $"{context}: the converted avatar must be upright with its feet below its hips.");
        toesPosition.Z.ShouldBeLessThan(
            feetPosition.Z - 0.01f,
            $"{context}: Unity +Z-forward must be reflected exactly once to XRENGINE -Z-forward.");

        TestContext.Progress.WriteLine(
            $"{context} skin audit: components={components.Length}, lods={runtimeLodCount}, distinctMeshes={distinctMeshes.Length}, skinnedMeshes={skinnedMeshes.Length}, vertices={totalVertices}, maxBoneIdentityError={maximumBoneIdentityError:F8}, maxVertexBindDisplacement={maximumVertexBindDisplacement:F8}, maxWeightSumError={maximumWeightSumError:F8}.");
        TestContext.Progress.WriteLine(
            $"{context} bind orientation: hips={hipsPosition}, head={headPosition}, feet={feetPosition}, toes={toesPosition}; forwardDeltaZ={(toesPosition.Z - feetPosition.Z):F6}.");
    }

    private static (long AlignedFaces, long OpposedFaces, string WorstMesh, float WorstAlignedRatio)
        AuditTriangleWindingAgainstNormals(IEnumerable<XRMesh> meshes)
    {
        long alignedFaces = 0;
        long opposedFaces = 0;
        string worstMesh = "<none>";
        float worstAlignedRatio = 1.0f;

        foreach (XRMesh mesh in meshes)
        {
            if (!mesh.HasNormals || mesh.Triangles is not { Count: > 0 } triangles)
                continue;

            long meshAlignedFaces = 0;
            long meshOpposedFaces = 0;
            foreach (IndexTriangle triangle in triangles)
            {
                if (triangle.Point0 < 0 || triangle.Point1 < 0 || triangle.Point2 < 0)
                    continue;

                Vector3 p0 = mesh.GetPosition((uint)triangle.Point0);
                Vector3 p1 = mesh.GetPosition((uint)triangle.Point1);
                Vector3 p2 = mesh.GetPosition((uint)triangle.Point2);
                Vector3 faceNormal = Vector3.Cross(p1 - p0, p2 - p0);
                Vector3 vertexNormal =
                    mesh.GetNormal((uint)triangle.Point0) +
                    mesh.GetNormal((uint)triangle.Point1) +
                    mesh.GetNormal((uint)triangle.Point2);
                if (faceNormal.LengthSquared() <= 1.0e-12f || vertexNormal.LengthSquared() <= 1.0e-12f)
                    continue;

                if (Vector3.Dot(faceNormal, vertexNormal) >= 0.0f)
                    meshAlignedFaces++;
                else
                    meshOpposedFaces++;
            }

            alignedFaces += meshAlignedFaces;
            opposedFaces += meshOpposedFaces;
            long classifiedFaces = meshAlignedFaces + meshOpposedFaces;
            if (classifiedFaces == 0)
                continue;

            float alignedRatio = (float)meshAlignedFaces / classifiedFaces;
            if (alignedRatio >= worstAlignedRatio)
                continue;

            worstAlignedRatio = alignedRatio;
            worstMesh = mesh.Name ?? mesh.ID.ToString();
        }

        return (alignedFaces, opposedFaces, worstMesh, worstAlignedRatio);
    }

    private static SceneNode FindNodeByPath(SceneNode root, params string[] path)
    {
        SceneNode current = root;
        foreach (string segment in path)
        {
            current = current.Transform.Children
                .Select(static transform => transform.SceneNode)
                .Where(static node => node is not null)
                .Cast<SceneNode>()
                .Single(node => string.Equals(node.Name, segment, StringComparison.Ordinal));
        }

        return current;
    }

    [Test]
    [Category("PrivateIntegration")]
    public void Jax2026_HairSourceMaterialMapsSourceToonRimWithoutDefaultSubstitution()
    {
        string fixturePath = Environment.GetEnvironmentVariable("XRE_UNITY_AVATAR_FIXTURE")
            ?? DefaultPrivateFixturePath;
        if (!File.Exists(fixturePath))
        {
            Assert.Ignore(
                "Private Unity avatar corpus is unavailable. Set XRE_UNITY_AVATAR_FIXTURE to jax2026.prefab to run this opt-in integration test.");
        }

        string projectRoot = SourceProjectLocator.Locate(fixturePath).ProjectRoot;
        string sourceMaterialPath = Path.Combine(projectRoot, "Assets", "1 Hair 2.mat");
        File.Exists(sourceMaterialPath).ShouldBeTrue();

        SerializedMaterialImportResult result = SerializedMaterialImporter.ImportWithReport(
            sourceMaterialPath,
            projectRoot);
        XRMaterial material = result.Material.ShouldNotBeNull();
        result.SourceDocument.ShouldNotBeNull()
            .TryGetFloat("_RimSharpness", out float sourceRimSharpness)
            .ShouldBeTrue();

        float convertedRimSharpness = material.Parameter<ShaderFloat>("_RimSharpness")
            .ShouldNotBeNull().Value;
        float convertedRimEmission = material.Parameter<ShaderFloat>("_RimEmission")
            .ShouldNotBeNull().Value;
        float convertedRimWidth = material.Parameter<ShaderFloat>("_RimWidth")
            .ShouldNotBeNull().Value;

        sourceRimSharpness.ShouldBe(0.0f);
        convertedRimSharpness.ShouldBe(0.0f);
        convertedRimEmission.ShouldBe(0.0f);
        convertedRimWidth.ShouldBe(0.154f, 0.0001f);
        TestContext.Progress.WriteLine(
            $"Hair source rim: sharpness={convertedRimSharpness}, emission={convertedRimEmission}, width={convertedRimWidth}.");
    }

    [Test]
    [Category("PrivateIntegration")]
    public void Jax2026_HairThemeVariantRetainsAnimatedThemeUniforms()
    {
        string? nativeAssetPath =
            Environment.GetEnvironmentVariable("XRE_UNITY_AVATAR_NATIVE_ASSET");
        if (string.IsNullOrWhiteSpace(nativeAssetPath) || !File.Exists(nativeAssetPath))
        {
            Assert.Ignore(
                "Externalized private Unity avatar output is unavailable. Set XRE_UNITY_AVATAR_NATIVE_ASSET to the generated jax2026.asset to run this opt-in integration test.");
        }

        string materialPath = Path.Combine(
            Path.GetDirectoryName(nativeAssetPath)!,
            "jax2026",
            "Materials",
            "1 Hair 2.asset");
        if (!File.Exists(materialPath))
            Assert.Fail($"Representative native avatar material is missing: {materialPath}");

        XRMaterial material = Engine.Assets
            .Load<XRMaterial>(materialPath, bypassJobThread: true)
            .ShouldNotBeNull();
        material.PrepareUberVariantImmediately()
            .ShouldBeTrue(material.UberVariantStatus.FailureReason);

        material.ActiveUberVariant.EnabledFeatures.ShouldContain("global-masks-themes");
        material.ActiveUberVariant.AnimatedProperties.ShouldContain("_GlobalThemeColor0");
        material.ActiveUberVariant.AnimatedProperties.ShouldContain("_GlobalThemeAdjust0");
        material.ActiveUberVariant.StaticProperties.ShouldContain("_ColorThemeIndex=1");
        material.ActiveUberVariant.StaticProperties.ShouldContain("_EmissionColorThemeIndex=2");

        XRShader fragment = material.GetShader(EShaderType.Fragment).ShouldNotBeNull();
        string source = fragment.Source.ShouldNotBeNull().Text.ShouldNotBeNull();
        source.ShouldContain("uniform vec4 _GlobalThemeColor0;");
        source.ShouldContain("uniform vec3 _GlobalThemeAdjust0;");
        source.ShouldContain("theme = _GlobalThemeColor0;");
        source.ShouldNotContain("#define XRENGINE_UBER_DISABLE_GLOBAL_MASKS_THEMES 1");

        string textureSummary = string.Join(
            ", ",
            material.Textures.Select(
                (texture, index) => $"{index}:{texture?.Name ?? "<null>"}->{texture?.SamplerName ?? "<indexed>"}"));
        XRTexture mainTexture = material.Textures
            .FirstOrDefault(texture => string.Equals(texture?.SamplerName, "_MainTex", StringComparison.Ordinal))
            .ShouldNotBeNull($"Reloaded Hair texture bindings: {textureSummary}");
        XRTexture emissionTexture = material.Textures
            .FirstOrDefault(texture => string.Equals(texture?.SamplerName, "_EmissionMap", StringComparison.Ordinal))
            .ShouldNotBeNull($"Reloaded Hair texture bindings: {textureSummary}");

        TestContext.Progress.WriteLine(
            $"Hair theme variant 0x{material.ActiveUberVariant.VariantHash:x16}: " +
            $"sourceBytes={source.Length}, sourceLines={source.Count(static character => character == '\n') + 1}, " +
            $"animated={material.ActiveUberVariant.AnimatedProperties.Length}, static={material.ActiveUberVariant.StaticProperties.Length}.");
        TestContext.Progress.WriteLine(
            $"Hair textures: {textureSummary}; " +
            $"main={mainTexture.ID}, emission={emissionTexture.ID}.");
    }

    [Test]
    [Category("PrivateIntegration")]
    public void Jax2026_ExternalizedUberMaterialReloadsAndCompiles()
    {
        string? nativeAssetPath =
            Environment.GetEnvironmentVariable("XRE_UNITY_AVATAR_NATIVE_ASSET");
        if (string.IsNullOrWhiteSpace(nativeAssetPath) || !File.Exists(nativeAssetPath))
        {
            Assert.Ignore(
                "Externalized private Unity avatar output is unavailable. Set XRE_UNITY_AVATAR_NATIVE_ASSET to the generated jax2026.asset to run this opt-in integration test.");
        }

        string materialPath = Path.Combine(
            Path.GetDirectoryName(nativeAssetPath)!,
            "jax2026",
            "Materials",
            "1 Hair 3.asset");
        if (!File.Exists(materialPath))
            Assert.Fail($"Representative native avatar material is missing: {materialPath}");

        XRMaterial material = Engine.Assets
            .Load<XRMaterial>(materialPath, bypassJobThread: true)
            .ShouldNotBeNull();
        string beforeSummary = DescribeParameters(material.Parameters);
        material.EnsureUberStateInitialized();
        string afterSummary = DescribeParameters(material.Parameters);
        material.Parameter<ShaderInt>("_MainTexStochasticMode").ShouldNotBeNull(
            $"Material '{material.Name}' loaded {material.Parameters.Length} parameters.{Environment.NewLine}" +
            $"Before EnsureUberStateInitialized:{Environment.NewLine}{beforeSummary}{Environment.NewLine}" +
            $"After EnsureUberStateInitialized:{Environment.NewLine}{afterSummary}");
        material.Parameter<ShaderInt>("_MainTexDistortionMapUV").ShouldNotBeNull();
        material.Parameter<ShaderVector2>("_MainTexDistortionSpeed").ShouldNotBeNull();
        material.Parameter<ShaderVector4>("_MainTexDistortionMap_ST").ShouldNotBeNull();
        material.Parameter<ShaderVector4>("_GlobalMaskMin").ShouldNotBeNull();
        material.Parameter<ShaderVector2>("_UvTileDiscardGrid").ShouldNotBeNull();
        material.Parameter<ShaderVector4>("_PathingParams").ShouldNotBeNull();
        material.Parameter<ShaderVector4>("_LightDataAOStrengths").ShouldNotBeNull();
        material.PrepareUberVariantImmediately()
            .ShouldBeTrue(material.UberVariantStatus.FailureReason);

        XRShader fragment = material.GetShader(EShaderType.Fragment).ShouldNotBeNull();
        string source = fragment.Source.ShouldNotBeNull().Text.ShouldNotBeNull();
        source.ShouldContain("getUV(0, mesh)");
        source.ShouldNotContain("getUV(0.0, mesh)");
        source.ShouldContain("vec2(0.0, 0.0)");
        source.ShouldNotContain("vec2 grid = max(0.0, vec2(1.0));");
        byte[] spirv = VulkanShaderCompiler.Compile(
            fragment,
            out string entryPoint,
            out _,
            out string? rewritten);
        spirv.Length.ShouldBeGreaterThan(20);
        entryPoint.ShouldBe("main");
        rewritten.ShouldNotBeNullOrWhiteSpace();
    }

    private static string DescribeParameters(IEnumerable<ShaderVar> parameters)
        => string.Join(
            Environment.NewLine,
            parameters
                .Where(static parameter =>
                    parameter.Name?.Contains("MainTex", StringComparison.Ordinal) == true ||
                    parameter.Name?.Contains("GlobalMask", StringComparison.Ordinal) == true ||
                    parameter.Name?.Contains("Poi", StringComparison.Ordinal) == true ||
                    parameter.Name?.Contains("LightDataAO", StringComparison.Ordinal) == true)
                .Select(static parameter => $"{parameter.Name}: {parameter.GetType().Name}"));

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

        SerializedPrefabConversionResult conversion = SerializedSceneImporter.ImportPrefabWithManifest(fixturePath);
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
        IEnumerable<SourceImportDependencyManifestEntry> dependencies,
        string extension)
        => dependencies.Count(dependency =>
            string.Equals(
                Path.GetExtension(dependency.NormalizedPath),
                extension,
                StringComparison.OrdinalIgnoreCase));

    private static bool IsTextureDependency(string path)
        => Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".exr" or ".gif" or ".psd" or ".hdr";

    private static bool IsEffectivelyActive(SceneNode node)
    {
        SceneNode? current = node;
        while (current is not null)
        {
            if (!current.IsActiveSelf)
                return false;

            current = current.Parent;
        }

        return true;
    }

    private static void ClearAssetCaches()
    {
        Engine.Assets.LoadedAssetsByPathInternal.Clear();
        Engine.Assets.LoadedAssetsByOriginalPathInternal.Clear();
        Engine.Assets.LoadedAssetsByIDInternal.Clear();
    }

    private static void AssertAuthoredPrefabFidelity(
        string fixturePath,
        SceneNode root,
        SerializedPrefabImportManifest manifest)
    {
        PrivatePrefabModification[] modifications = ParsePrefabModifications(fixturePath);
        PrivatePrefabModification[] materialOverrides =
        [
            .. modifications.Where(static modification =>
                modification.PropertyPath.StartsWith(
                    "m_Materials.Array.data[",
                    StringComparison.Ordinal)),
        ];
        PrivatePrefabModification[] blendShapeOverrides =
        [
            .. modifications.Where(static modification =>
                modification.PropertyPath.StartsWith(
                    "m_BlendShapeWeights.Array.data[",
                    StringComparison.Ordinal)),
        ];
        PrivatePrefabModification[] activeOverrides =
        [
            .. modifications.Where(static modification =>
                modification.PropertyPath == "m_IsActive"),
        ];

        materialOverrides.Length.ShouldBe(75);
        materialOverrides
            .Select(static modification => modification.ObjectGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ShouldBe(31);
        blendShapeOverrides.Length.ShouldBe(6);
        activeOverrides.Length.ShouldBe(33);

        string mainModelGuid = materialOverrides
            .Select(static modification => modification.TargetGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Single();
        var context = new SourceProjectImportContext(fixturePath);
        string mainModelPath = context.GuidIndex.ResolvePath(mainModelGuid).ShouldNotBeNull();
        SerializedModelImporterDocument modelMetadata =
            SerializedModelImporterDocumentParser.ParseForModel(mainModelPath);
        modelMetadata.ExternalMaterialRemaps.Count.ShouldBe(55);
        foreach (SourceExternalMaterialRemap remap in modelMetadata.ExternalMaterialRemaps)
        {
            string remapPath = context.GuidIndex.ResolvePath(remap.TargetMaterial.Guid).ShouldNotBeNull();
            File.Exists(remapPath).ShouldBeTrue(remapPath);
            manifest.Dependencies.ShouldContain(dependency =>
                string.Equals(
                    dependency.SourceGuid,
                    remap.TargetMaterial.Guid,
                    StringComparison.OrdinalIgnoreCase) &&
                (dependency.Outcome == SourceImportConversionOutcome.Converted ||
                 dependency.Outcome == SourceImportConversionOutcome.Downgraded));
        }

        var gameObjectsByFileId = new Dictionary<long, SceneNode>();
        var transformsByFileId = new Dictionary<long, Transform>();
        var renderersByFileId = new Dictionary<long, ModelComponent>();
        IndexModelHierarchy(
            root,
            "//RootNode/root",
            gameObjectsByFileId,
            transformsByFileId,
            renderersByFileId);

        PrivatePrefabModification[] staleMaterialOverrides =
        [
            .. materialOverrides.Where(modification =>
                !renderersByFileId.ContainsKey(modification.TargetFileId)),
        ];
        long[] staleRendererTargets =
        [
            .. staleMaterialOverrides
                .Select(static modification => modification.TargetFileId)
                .Distinct()
                .Order(),
        ];
        staleMaterialOverrides.Length.ShouldBe(3);
        staleRendererTargets.Length.ShouldBe(2);
        AssertStaleTargetsDiagnosed(manifest, staleRendererTargets);

        foreach (PrivatePrefabModification modification in materialOverrides.Except(staleMaterialOverrides))
        {
            renderersByFileId.TryGetValue(
                    modification.TargetFileId,
                    out ModelComponent? component)
                .ShouldBeTrue();
            Model model = component.ShouldNotBeNull().Model.ShouldNotBeNull();
            int slot = ParseArrayIndex(modification.PropertyPath);
            slot.ShouldBeInRange(0, model.Meshes.Count - 1);
            string expectedPath =
                context.GuidIndex.ResolvePath(modification.ObjectGuid).ShouldNotBeNull();
            foreach (SubMeshLOD lod in model.Meshes[slot].LODs)
            {
                string actualPath = lod.Material.ShouldNotBeNull().OriginalPath.ShouldNotBeNull();
                Path.GetFullPath(actualPath).ShouldBe(
                    Path.GetFullPath(expectedPath),
                    StringCompareShould.IgnoreCase);
            }
        }

        var expectedBlendShapeTargets = new Dictionary<long, string>
        {
            [-8616136768033072943L] = "CLO Tank",
            [1630794972795428178L] = "Body",
            [4889757301359932635L] = "Fishnet",
            [6868716418057165105L] = "Face",
        };
        foreach (PrivatePrefabModification modification in blendShapeOverrides)
        {
            renderersByFileId.TryGetValue(
                    modification.TargetFileId,
                    out ModelComponent? component)
                .ShouldBeTrue();
            expectedBlendShapeTargets.TryGetValue(
                    modification.TargetFileId,
                    out string? expectedNodeName)
                .ShouldBeTrue();
            component.ShouldNotBeNull().SceneNode.ShouldNotBeNull().Name.ShouldBe(expectedNodeName);

            int index = ParseArrayIndex(modification.PropertyPath);
            component.DefaultBlendShapeWeights.TryGetValue(index, out float actualWeight)
                .ShouldBeTrue();
            actualWeight.ShouldBe(ParseFloat(modification.Value), 0.0001f);
        }

        PrivatePrefabModification[] staleActiveOverrides =
        [
            .. activeOverrides.Where(modification =>
                !gameObjectsByFileId.ContainsKey(modification.TargetFileId)),
        ];
        staleActiveOverrides.Length.ShouldBe(2);
        AssertStaleTargetsDiagnosed(
            manifest,
            staleActiveOverrides
                .Select(static modification => modification.TargetFileId)
                .Distinct());
        foreach (PrivatePrefabModification modification in activeOverrides.Except(staleActiveOverrides))
        {
            gameObjectsByFileId.TryGetValue(
                    modification.TargetFileId,
                    out SceneNode? node)
                .ShouldBeTrue();
            node.ShouldNotBeNull().IsActiveSelf.ShouldBe(
                modification.Value.Trim() != "0");
        }

        PrivatePrefabModification[] transformOverrides =
        [
            .. modifications.Where(modification =>
                string.Equals(
                    modification.TargetGuid,
                    mainModelGuid,
                    StringComparison.OrdinalIgnoreCase) &&
                (modification.PropertyPath.StartsWith("m_LocalPosition.", StringComparison.Ordinal) ||
                 modification.PropertyPath.StartsWith("m_LocalRotation.", StringComparison.Ordinal) ||
                 modification.PropertyPath.StartsWith("m_LocalScale.", StringComparison.Ordinal))),
        ];
        transformOverrides.Length.ShouldBe(28);
        foreach (PrivatePrefabModification modification in transformOverrides)
        {
            transformsByFileId.TryGetValue(
                    modification.TargetFileId,
                    out Transform? transform)
                .ShouldBeTrue();
            float expectedValue = ConvertTransformCoordinate(
                ParseFloat(modification.Value),
                modification.PropertyPath);
            ReadConvertedTransformCoordinate(
                    transform.ShouldNotBeNull(),
                    modification.PropertyPath)
                .ShouldBe(expectedValue, 0.0015f);
        }
    }

    private static void AssertStaleTargetsDiagnosed(
        SerializedPrefabImportManifest manifest,
        IEnumerable<long> staleTargets)
    {
        foreach (long staleTarget in staleTargets)
        {
            manifest.Diagnostics.ShouldContain(diagnostic =>
                diagnostic.Code == "UNITYOVERRIDE0003" &&
                diagnostic.SourceIdentity != null &&
                diagnostic.SourceIdentity.LocalFileId == staleTarget);
        }
    }

    private static PrivatePrefabModification[] ParsePrefabModifications(string path)
    {
        const string pattern =
            """
            (?ms)^\s*-\s+target:\s*
            \{fileID:\s*(?<target>-?\d+),\s*
            guid:\s*(?<targetGuid>[0-9a-fA-F]{32}),\s*
            type:\s*\d+\}\s*
            propertyPath:\s*(?<path>[^\r\n]+)\s*
            value:(?<value>[^\r\n]*)\s*
            objectReference:\s*
            \{fileID:\s*(?<objectFile>-?\d+)
            (?:,\s*guid:\s*(?<objectGuid>[0-9a-fA-F]{32}),\s*type:\s*\d+)?\}
            """;
        return
        [
            .. Regex.Matches(
                    File.ReadAllText(path),
                    pattern,
                    RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)
                .Select(static match => new PrivatePrefabModification(
                    long.Parse(match.Groups["target"].Value, CultureInfo.InvariantCulture),
                    match.Groups["targetGuid"].Value,
                    match.Groups["path"].Value.Trim(),
                    match.Groups["value"].Value.Trim(),
                    long.Parse(match.Groups["objectFile"].Value, CultureInfo.InvariantCulture),
                    match.Groups["objectGuid"].Value)),
        ];
    }

    private static void IndexModelHierarchy(
        SceneNode node,
        string sourcePath,
        Dictionary<long, SceneNode> gameObjects,
        Dictionary<long, Transform> transforms,
        Dictionary<long, ModelComponent> renderers)
    {
        gameObjects.TryAdd(SerializedModelFileId.ForGameObject(sourcePath), node);
        if (node.Transform is Transform transform)
            transforms.TryAdd(SerializedModelFileId.ForTransform(sourcePath), transform);

        foreach (ModelComponent component in node.GetComponents<ModelComponent>())
        {
            bool skinned = component.Model?.Meshes
                .SelectMany(static mesh => mesh.LODs)
                .Any(static lod =>
                    lod.Mesh is { } mesh &&
                    (mesh.HasSkinning || mesh.HasBlendshapes)) == true;
            string rendererType = skinned ? "SkinnedMeshRenderer" : "MeshRenderer";
            renderers.TryAdd(
                SerializedModelFileId.ForComponent(rendererType, sourcePath),
                component);
        }

        foreach (TransformBase childTransform in node.Transform.Children)
        {
            if (childTransform.SceneNode is not SceneNode child)
                continue;

            IndexModelHierarchy(
                child,
                $"{sourcePath}/{child.Name ?? SceneNode.DefaultName}",
                gameObjects,
                transforms,
                renderers);
        }
    }

    private static int ParseArrayIndex(string propertyPath)
    {
        int open = propertyPath.LastIndexOf('[');
        int close = propertyPath.LastIndexOf(']');
        return open >= 0 && close > open &&
            int.TryParse(
                propertyPath.AsSpan(open + 1, close - open - 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int index)
            ? index
            : -1;
    }

    private static float ParseFloat(string value)
        => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static float ReadConvertedTransformCoordinate(
        Transform transform,
        string propertyPath)
    {
        char axis = propertyPath[^1];
        if (propertyPath.StartsWith("m_LocalPosition.", StringComparison.Ordinal))
        {
            return axis switch
            {
                'x' => transform.Translation.X,
                'y' => transform.Translation.Y,
                'z' => transform.Translation.Z,
                _ => float.NaN,
            };
        }

        if (propertyPath.StartsWith("m_LocalScale.", StringComparison.Ordinal))
        {
            return axis switch
            {
                'x' => transform.Scale.X,
                'y' => transform.Scale.Y,
                'z' => transform.Scale.Z,
                _ => float.NaN,
            };
        }

        return axis switch
        {
            'x' => transform.Rotation.X,
            'y' => transform.Rotation.Y,
            'z' => transform.Rotation.Z,
            'w' => transform.Rotation.W,
            _ => float.NaN,
        };
    }

    private static float ConvertTransformCoordinate(
        float value,
        string propertyPath)
    {
        char axis = propertyPath[^1];
        if (propertyPath.StartsWith("m_LocalPosition.", StringComparison.Ordinal))
            return axis == 'z' ? -value : value;
        if (propertyPath.StartsWith("m_LocalRotation.", StringComparison.Ordinal))
            return axis is 'x' or 'y' ? -value : value;
        return value;
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

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record PrivatePrefabModification(
        long TargetFileId,
        string TargetGuid,
        string PropertyPath,
        string Value,
        long ObjectFileId,
        string ObjectGuid);

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
