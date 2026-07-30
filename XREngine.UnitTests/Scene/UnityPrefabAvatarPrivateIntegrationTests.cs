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
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Vulkan;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

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

        AssertAuthoredPrefabFidelity(fixturePath, root, manifest);

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

    private static void AssertAuthoredPrefabFidelity(
        string fixturePath,
        SceneNode root,
        UnityPrefabImportManifest manifest)
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
        var context = new UnityProjectImportContext(fixturePath);
        string mainModelPath = context.GuidIndex.ResolvePath(mainModelGuid).ShouldNotBeNull();
        UnityModelImporterDocument modelMetadata =
            UnityModelImporterDocumentParser.ParseForModel(mainModelPath);
        modelMetadata.ExternalMaterialRemaps.Count.ShouldBe(55);
        foreach (UnityExternalMaterialRemap remap in modelMetadata.ExternalMaterialRemaps)
        {
            string remapPath = context.GuidIndex.ResolvePath(remap.TargetMaterial.Guid).ShouldNotBeNull();
            File.Exists(remapPath).ShouldBeTrue(remapPath);
            manifest.Dependencies.ShouldContain(dependency =>
                string.Equals(
                    dependency.SourceGuid,
                    remap.TargetMaterial.Guid,
                    StringComparison.OrdinalIgnoreCase) &&
                (dependency.Outcome == UnityImportConversionOutcome.Converted ||
                 dependency.Outcome == UnityImportConversionOutcome.Downgraded));
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
        UnityPrefabImportManifest manifest,
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
        string unityPath,
        Dictionary<long, SceneNode> gameObjects,
        Dictionary<long, Transform> transforms,
        Dictionary<long, ModelComponent> renderers)
    {
        gameObjects.TryAdd(UnityModelFileId.ForGameObject(unityPath), node);
        if (node.Transform is Transform transform)
            transforms.TryAdd(UnityModelFileId.ForTransform(unityPath), transform);

        foreach (ModelComponent component in node.GetComponents<ModelComponent>())
        {
            bool skinned = component.Model?.Meshes
                .SelectMany(static mesh => mesh.LODs)
                .Any(static lod =>
                    lod.Mesh is { } mesh &&
                    (mesh.HasSkinning || mesh.HasBlendshapes)) == true;
            string rendererType = skinned ? "SkinnedMeshRenderer" : "MeshRenderer";
            renderers.TryAdd(
                UnityModelFileId.ForComponent(rendererType, unityPath),
                component);
        }

        foreach (TransformBase childTransform in node.Transform.Children)
        {
            if (childTransform.SceneNode is not SceneNode child)
                continue;

            IndexModelHierarchy(
                child,
                $"{unityPath}/{child.Name ?? SceneNode.DefaultName}",
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
