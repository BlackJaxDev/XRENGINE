using System.Collections;
using System.Diagnostics;
using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Importers.SourceToon;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static partial class Models
    {
        private sealed record SerializedPrefabStartupImportResult(
            SceneNode RootNode,
            SceneNode AvatarRoot,
            int ModelCount,
            int MaterialCount,
            int SourceToonMaterialCount,
            int SourceToonDowngradeCount,
            string SourceContentSha256,
            string? PrimaryModelPath);

        /// <summary>
        /// Adds one source file as an independent scene in the startup world.
        /// </summary>
        private static void PublishSerializedImportScene(SceneNode startupRoot, XRScene scene)
        {
            if (startupRoot.IsDestroyed || startupRoot.World is not RuntimeWorld world ||
                world.IsDisposing || world.TargetWorld is not XRWorld targetWorld)
            {
                throw new InvalidOperationException("The startup world is no longer available for Unity scene import.");
            }

            string baseName = scene.Name ?? "Unity Import";
            int suffix = 2;
            while (targetWorld.Scenes.Any(existing =>
                string.Equals(existing.Name, scene.Name, StringComparison.OrdinalIgnoreCase)))
            {
                scene.Name = $"{baseName} ({suffix++})";
            }

            targetWorld.Scenes.Add(scene);
            try
            {
                world.LoadScene(scene);
            }
            catch
            {
                // A failed rollback keeps the scene registered for a later cleanup attempt.
                if (!world.IsSceneLoaded(scene))
                    targetWorld.Scenes.Remove(scene);
                throw;
            }
        }

        private static bool IsSerializedPrefabPath(string path)
            => string.Equals(Path.GetExtension(path), ".prefab", StringComparison.OrdinalIgnoreCase);

        private static bool IsSerializedHierarchyPath(string path)
            => IsSerializedPrefabPath(path) ||
                string.Equals(Path.GetExtension(path), ".unity", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Schedules the synchronous Unity project converter on an engine worker. The converter owns
        /// Unity dependency resolution and source-aware material conversion; generic Assimp material
        /// factories are intentionally not involved in this path.
        /// </summary>
        private static void ScheduleSerializedPrefabImport(
            string prefabPath,
            Settings.ModelImportSettings settings,
            Action<SerializedPrefabStartupImportResult> onFinished,
            Action<Exception> onError,
            string? sourceProjectRoot = null)
        {
            SerializedPrefabStartupImportResult? result = null;
            long lastProgressLogTimestamp = 0;

            void ReportProgress(float progress, string message)
            {
                long now = Stopwatch.GetTimestamp();
                long previous = Volatile.Read(ref lastProgressLogTimestamp);
                bool completed = progress >= 1.0f;
                if (!completed && previous != 0 &&
                    now - previous < Stopwatch.Frequency / 4)
                {
                    return;
                }

                if (!completed && Interlocked.CompareExchange(
                        ref lastProgressLogTimestamp,
                        now,
                        previous) != previous)
                {
                    return;
                }

                if (completed)
                    Volatile.Write(ref lastProgressLogTimestamp, now);
                Debug.Meshes(
                    $"[UnityPrefab] Progress ({Path.GetFileName(prefabPath)}): {progress:P0} {message}");
            }

            IEnumerable ImportRoutine()
            {
                string? projectRoot = sourceProjectRoot ?? ResolveSourceProjectRoot(settings.SourceProjectRoot);
                Debug.Meshes(
                    $"[UnityPrefab] Converting '{prefabPath}' with the Unity project importer. " +
                    "Recognized source toon materials will use the XRENGINE Uber shader.");
                if (settings.ZUp)
                {
                    Debug.MeshesWarning(
                        $"[UnityPrefab] ZUp is ignored for '{prefabPath}' because Unity prefab and model " +
                        "coordinate conversion is driven by Unity importer metadata.");
                }

                SerializedPrefabConversionResult conversion = SerializedSceneImporter.ImportPrefabWithManifest(
                    prefabPath,
                    outputDestination: null,
                    explicitProjectOrAssetsRoot: projectRoot,
                    cancellationToken: default,
                    progress: ReportProgress,
                    fbxBackend: ResolveFbxBackend(settings),
                    allowPartialImport: settings.AllowPartialImport,
                    sceneRootName: settings.SceneRootName);

                SceneNode convertedRoot = conversion.RootNode
                    ?? throw new SourceVisualImportException(
                        $"Unity prefab conversion returned no hierarchy for '{prefabPath}'.");
                SerializedPrefabImportManifest manifest = conversion.Manifest
                    ?? throw new SourceVisualImportException(
                        $"Unity prefab conversion returned no manifest for '{prefabPath}'.");
                if (IsSerializedPrefabPath(prefabPath))
                    ValidateSerializedPrefabCompletion(manifest, settings, prefabPath);

                (int modelCount, int materialCount, int sourceToonCount, int downgradeCount) =
                    ValidateSerializedPrefabMaterials(convertedRoot, prefabPath);
                // Start background Uber shader variant compilation and pack managed GPU deformation
                // payloads now while the hierarchy is still owned by the import worker so meshes do
                // not stall the render thread after publication.
                PrewarmSerializedPrefabAssets(convertedRoot);
                if (manifest.CompletionTier == SourceImportCompletionTier.PartialVisual)
                    Debug.MeshesWarning($"[UnityPrefab] Partial import '{prefabPath}': {manifest.Diagnostics.Count} diagnostic(s). Available content is retained.");
                SceneNode importRoot = ApplySerializedPrefabImportTransform(convertedRoot, settings);
                int layer = settings.Kind is UnitTestModelImportKind.Static
                    ? DefaultLayers.StaticIndex
                    : DefaultLayers.DynamicIndex;
                importRoot.IterateHierarchy(node => node.Layer = layer);

                (string? primaryModelPath, _) = SerializedHumanoidAvatarSetup.FindPrimaryModelDependency(manifest);
                result = new SerializedPrefabStartupImportResult(
                    importRoot,
                    convertedRoot,
                    modelCount,
                    materialCount,
                    sourceToonCount,
                    downgradeCount,
                    manifest.ComputeSourceContentSha256(),
                    primaryModelPath);
                yield return new JobProgress(1.0f, result);
            }

            try
            {
                _ = Engine.Jobs.Schedule(
                    ImportRoutine(),
                    progress: null,
                    completed: () =>
                    {
                        if (result is null)
                        {
                            onError(new SourceVisualImportException(
                                $"Unity prefab import completed without a result for '{prefabPath}'."));
                            return;
                        }

                        onFinished(result);
                    },
                    error: onError);
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        }

        /// <summary>
        /// Keeps imported mesh renderers out of the visible scene while the hierarchy is
        /// attached. They are activated in small cohorts so descriptor and mesh preparation
        /// cannot turn one prefab publication into a single unbounded render-thread spike.
        /// </summary>
        private static List<ModelComponent> SuspendSerializedPrefabModelPublication(
            SceneNode rootNode)
        {
            List<ModelComponent> suspended = [];
            rootNode.IterateComponents<ModelComponent>(component =>
            {
                if (!component.IsActive)
                    return;

                component.IsActive = false;
                suspended.Add(component);
            }, iterateChildHierarchy: true);
            return suspended;
        }

        private static void PrewarmSerializedPrefabAssets(SceneNode rootNode)
        {
            PrewarmSerializedPrefabMaterials(rootNode);

            HashSet<XRMesh> visitedMeshes = new(ReferenceEqualityComparer.Instance);
            rootNode.IterateComponents<ModelComponent>(component =>
            {
                if (component.Model is { } model)
                {
                    for (int i = 0; i < model.Meshes.Count; i++)
                    {
                        foreach (SubMeshLOD lod in model.Meshes[i].LODs)
                        {
                            if (lod.Mesh is { } lodMesh)
                                visitedMeshes.Add(lodMesh);
                        }
                    }
                }

                for (int i = 0; i < component.Meshes.Count; i++)
                {
                    if (component.Meshes[i].CurrentLODRenderer?.Mesh is { } currentMesh)
                        visitedMeshes.Add(currentMesh);
                }
            }, iterateChildHierarchy: true);

            foreach (XRMesh mesh in visitedMeshes)
            {
                if (!mesh.HasSkinning && !mesh.HasBlendshapes)
                    continue;

                try
                {
                    AdvancedGpuDeformationResources.PrepareImportedMesh(mesh);
                }
                catch (Exception ex)
                {
                    Debug.MeshesWarning(
                        $"[UnityPrefab] Skipping worker deformation pre-pack for mesh '{mesh.Name ?? "<unnamed>"}': {ex.Message}");
                }
            }
        }

        private static void PrewarmSerializedPrefabMaterials(SceneNode rootNode)
        {
            HashSet<XRMaterial> visited = new(ReferenceEqualityComparer.Instance);
            rootNode.IterateComponents<ModelComponent>(component =>
            {
                CollectModelMaterials(component, visited);
            }, iterateChildHierarchy: true);

            foreach (XRMaterial material in visited)
                material.RequestUberVariantPreparationIfNeeded();
        }

        private static void CollectModelMaterials(ModelComponent component, HashSet<XRMaterial> destination)
        {
            if (component.Model is { } model)
            {
                for (int i = 0; i < model.Meshes.Count; i++)
                {
                    SubMesh subMesh = model.Meshes[i];
                    foreach (SubMeshLOD lod in subMesh.LODs)
                    {
                        if (lod.Material is { } lodMaterial)
                            destination.Add(lodMaterial);
                    }
                }
            }

            for (int i = 0; i < component.Meshes.Count; i++)
            {
                RenderableMesh mesh = component.Meshes[i];
                if (mesh.MaterialOverride is { } overrideMaterial)
                    destination.Add(overrideMaterial);
                if (mesh.CurrentLODRenderer?.Material is { } lodMaterial)
                    destination.Add(lodMaterial);
            }
        }

        private static bool AreModelMaterialsReadyForAdmission(ModelComponent component)
        {
            if (component.Model is { } model)
            {
                for (int i = 0; i < model.Meshes.Count; i++)
                {
                    SubMesh subMesh = model.Meshes[i];
                    foreach (SubMeshLOD lod in subMesh.LODs)
                    {
                        if (lod.Material is { } lodMaterial && !lodMaterial.IsUberVariantReadyForAdmission)
                            return false;
                    }
                }
            }

            for (int i = 0; i < component.Meshes.Count; i++)
            {
                RenderableMesh mesh = component.Meshes[i];
                if (mesh.MaterialOverride is { } overrideMaterial && !overrideMaterial.IsUberVariantReadyForAdmission)
                    return false;
                if (mesh.CurrentLODRenderer?.Material is { } lodMaterial && !lodMaterial.IsUberVariantReadyForAdmission)
                    return false;
            }

            return true;
        }

        private static void StageSerializedPrefabModelPublication(
            SceneNode rootNode,
            List<ModelComponent> suspendedModels)
        {
            const int MaxModelsPerPresentedFrame = 4;
            const int MaxDeformingModelsPerPresentedFrame = 1;
            if (suspendedModels.Count == 0)
                return;

            PrewarmSerializedPrefabMaterials(rootNode);

            int nextModelIndex = 0;
            ulong lastActivationPresentFrameId = 0UL;
            long lastActivationTimestamp = 0L;
            long materialWaitStartTicks = Stopwatch.GetTimestamp();
            long materialWaitLimitTicks = Stopwatch.Frequency * 10L;
            long presentFallbackTicks = Stopwatch.Frequency;

            Debug.Meshes(
                $"[UnityPrefab] Staging {suspendedModels.Count} model components under " +
                $"'{rootNode.Name}' at up to {MaxModelsPerPresentedFrame} static / {MaxDeformingModelsPerPresentedFrame} deforming per presented frame.");
            Engine.AddAppThreadCoroutine(() =>
            {
                if (rootNode.IsDestroyed)
                    return true;

                // Wait until the renderer advances a frame after the previous model batch so new
                // cold meshes never pile up across consecutive rejected PresentNow attempts.
                // In VR, desktop presents can stay held while OpenXR renders, so use RenderFrameId.
                bool inVr = XREngine.Input.RuntimeVrStateServices.IsOpenXRActive || XREngine.Input.RuntimeVrStateServices.IsInVR;
                ulong currentPresentFrameId = inVr
                    ? RuntimeEngine.Rendering.State.RenderFrameId
                    : RuntimeEngine.Rendering.Stats.FrameLifecycle.PresentFrameId;
                long nowTicks = Stopwatch.GetTimestamp();
                if (lastActivationTimestamp != 0L
                    && currentPresentFrameId <= lastActivationPresentFrameId
                    && nowTicks - lastActivationTimestamp < presentFallbackTicks)
                {
                    return false;
                }

                // Skip any already-destroyed components before checking shader readiness.
                while (nextModelIndex < suspendedModels.Count && suspendedModels[nextModelIndex].IsDestroyed)
                    nextModelIndex++;

                if (nextModelIndex >= suspendedModels.Count)
                {
                    Debug.Meshes(
                        $"[UnityPrefab] Finished staged model publication for '{rootNode.Name}'.");
                    return true;
                }

                // Keep meshes hidden while their Uber shader variants are still compiling on
                // background threads so Vulkan PresentNow does not stall on cold shader builds.
                bool materialWaitExpired = nowTicks - materialWaitStartTicks >= materialWaitLimitTicks;
                int activatedThisFrame = 0;
                int deformingActivatedThisFrame = 0;
                while (nextModelIndex < suspendedModels.Count && activatedThisFrame < MaxModelsPerPresentedFrame)
                {
                    ModelComponent component = suspendedModels[nextModelIndex];
                    if (component.IsDestroyed)
                    {
                        nextModelIndex++;
                        continue;
                    }

                    bool isDeforming = HasDeformingMesh(component);
                    if (isDeforming && deformingActivatedThisFrame >= MaxDeformingModelsPerPresentedFrame)
                        break;

                    if (!materialWaitExpired && !AreModelMaterialsReadyForAdmission(component))
                        break;

                    component.IsActive = true;
                    nextModelIndex++;
                    activatedThisFrame++;
                    if (isDeforming)
                        deformingActivatedThisFrame++;
                }

                if (activatedThisFrame > 0)
                {
                    lastActivationPresentFrameId = currentPresentFrameId;
                    lastActivationTimestamp = nowTicks;
                }

                if (nextModelIndex < suspendedModels.Count)
                    return false;

                Debug.Meshes(
                    $"[UnityPrefab] Finished staged model publication for '{rootNode.Name}'.");
                return true;
            });
        }

        private static bool HasDeformingMesh(ModelComponent component)
        {
            if (component.Model is { } model)
            {
                for (int i = 0; i < model.Meshes.Count; i++)
                {
                    foreach (SubMeshLOD lod in model.Meshes[i].LODs)
                    {
                        if (lod.Mesh is { } lodMesh && (lodMesh.HasSkinning || lodMesh.HasBlendshapes))
                            return true;
                    }
                }
            }

            for (int i = 0; i < component.Meshes.Count; i++)
            {
                if (component.Meshes[i].CurrentLODRenderer?.Mesh is { } currentMesh &&
                    (currentMesh.HasSkinning || currentMesh.HasBlendshapes))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks that an animated prefab kept its avatar behavior. Strict imports fail only when
        /// an avatar component reports an error. Replaced materials and other visual downgrades lower
        /// the completion tier to <see cref="SourceImportCompletionTier.PartialVisual"/>, but they do not
        /// stop the import.
        /// </summary>
        private static void ValidateSerializedPrefabCompletion(
            SerializedPrefabImportManifest manifest,
            Settings.ModelImportSettings settings,
            string prefabPath)
        {
            if (settings.AllowPartialImport || settings.Kind is not UnitTestModelImportKind.Animated ||
                manifest.CompletionTier == SourceImportCompletionTier.VisualAndAvatarBehavior)
            {
                return;
            }

            string[] errors =
            [
                .. manifest.Diagnostics
                    .Where(static diagnostic =>
                        diagnostic.Category == SourceImportDiagnosticCategory.AvatarComponent &&
                        diagnostic.Severity == SourceImportDiagnosticSeverity.Error)
                    .Select(static diagnostic => diagnostic.ToString()),
            ];
            if (errors.Length == 0 && manifest.CompletionTier == SourceImportCompletionTier.PartialVisual)
                return;

            string details = errors.Length == 0
                ? "The converter did not report an avatar-behavior error detail."
                : string.Join("; ", errors);
            throw new SourceVisualImportException(
                $"Unity prefab '{prefabPath}' reached completion tier {manifest.CompletionTier}, but " +
                $"ModelsToImport Kind=Animated requires {SourceImportCompletionTier.VisualAndAvatarBehavior}. {details}");
        }

        private static string? ResolveSourceProjectRoot(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return null;

            string candidate = Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.Combine(Environment.CurrentDirectory, rawPath);
            return Path.GetFullPath(candidate);
        }

        private static SceneNode ApplySerializedPrefabImportTransform(
            SceneNode convertedRoot,
            Settings.ModelImportSettings settings)
        {
            if (!float.IsFinite(settings.Scale))
                throw new SourceVisualImportException(
                    $"Unity prefab import scale must be finite, but was {settings.Scale}.");

            bool hasScale = MathF.Abs(settings.Scale - 1.0f) > float.Epsilon;
            bool hasRotation = settings.YawPitchRoll is not null;
            bool hasTranslation = settings.Translation is not null;
            if (!hasScale && !hasRotation && !hasTranslation)
                return convertedRoot;

            var importRoot = new SceneNode($"{convertedRoot.Name} Import Root", new Transform());
            var transform = importRoot.GetTransformAs<Transform>(false)!;
            transform.Scale = new Vector3(settings.Scale);

            if (settings.YawPitchRoll is { } ypr)
            {
                transform.Rotation = Quaternion.CreateFromYawPitchRoll(
                    XRMath.DegToRad(ypr.Yaw),
                    XRMath.DegToRad(ypr.Pitch),
                    XRMath.DegToRad(ypr.Roll));
            }

            if (settings.Translation is { } translation)
                transform.Translation = new Vector3(translation.X, translation.Y, translation.Z);

            convertedRoot.Parent = importRoot;
            return importRoot;
        }

        /// <summary>
        /// Counts the models and materials in the converted prefab and checks that each source toon material
        /// reached the native Uber material path. A source toon material that used a fallback, an unknown
        /// shader version, or a missing Uber state produces a warning that names the material and the
        /// reason. These warnings never stop the import, because one material must not remove the rest
        /// of the avatar.
        /// </summary>
        private static (int ModelCount, int MaterialCount, int SourceToonCount, int DowngradeCount)
            ValidateSerializedPrefabMaterials(SceneNode rootNode, string prefabPath)
        {
            var models = new HashSet<Model>();
            var materials = new HashSet<XRMaterial>();
            rootNode.IterateComponents<ModelComponent>(component =>
            {
                if (component.Model is not Model model || !models.Add(model))
                    return;

                foreach (SubMesh subMesh in model.Meshes)
                    foreach (SubMeshLOD lod in subMesh.LODs)
                        if (lod.Material is not null)
                            materials.Add(lod.Material);
            }, iterateChildHierarchy: true);

            int sourceToonCount = 0;
            int downgradeCount = 0;
            foreach (XRMaterial material in materials)
            {
                if (!MaterialConversionReportRegistry.Instance.TryGet(material, out MaterialConversionReport report) ||
                    report.SourceShaderFamily is not (MaterialConversionReportBuilder.SourceToonFamilyName or MaterialConversionReportBuilder.ProToonFamilyName))
                {
                    continue;
                }

                bool unsupportedVersion = report.DiagnosticGroups
                    .SelectMany(static group => group.Diagnostics)
                    .Any(static diagnostic => diagnostic.Code == MaterialConversionDiagnosticCodes.UnknownVersion);
                if (unsupportedVersion)
                {
                    Debug.MeshesWarning(
                        $"[UnityPrefab] Source toon material '{report.MaterialName}' in '{prefabPath}' uses an unsupported " +
                        $"shader source ({report.SourceShaderVersion}). No verified source profile matched, so it uses " +
                        $"a fallback (outcome: {report.Outcome}). Inspect the material conversion report for its source identity.");
                    continue;
                }

                if (report.Outcome is EMaterialConversionOutcome.GenericFallback or EMaterialConversionOutcome.Failed)
                {
                    Debug.MeshesWarning(
                        $"[UnityPrefab] Source toon material '{report.MaterialName}' in '{prefabPath}' did not convert to the " +
                        $"native Uber shader and uses a fallback (outcome: {report.Outcome}). {string.Join(" ", report.Failures.Count > 0 ? report.Failures : report.Warnings)}");
                    continue;
                }

                if (!material.TryGetUberMaterialState(out _, out _))
                {
                    Debug.MeshesWarning(
                        $"[UnityPrefab] Source toon material '{report.MaterialName}' in '{prefabPath}' reported a successful " +
                        "conversion but has no native Uber shader state.");
                    continue;
                }

                if (report.Outcome == EMaterialConversionOutcome.ConvertedToSourceToon)
                    downgradeCount++;
                sourceToonCount++;
            }

            return (models.Count, materials.Count, sourceToonCount, downgradeCount);
        }
    }
}
