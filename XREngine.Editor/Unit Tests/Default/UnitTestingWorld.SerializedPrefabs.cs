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
            int ModelCount,
            int MaterialCount,
            int SourceToonMaterialCount,
            int SourceToonDowngradeCount,
            string SourceContentSha256);

        private static bool IsSerializedPrefabPath(string path)
            => string.Equals(Path.GetExtension(path), ".prefab", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Schedules the synchronous Unity project converter on an engine worker. The converter owns
        /// Unity dependency resolution and source-aware material conversion; generic Assimp material
        /// factories are intentionally not involved in this path.
        /// </summary>
        private static void ScheduleSerializedPrefabImport(
            string prefabPath,
            Settings.ModelImportSettings settings,
            Action<SerializedPrefabStartupImportResult> onFinished,
            Action<Exception> onError)
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
                string? projectRoot = ResolveSourceProjectRoot(settings.SourceProjectRoot);
                Debug.Meshes(
                    $"[UnityPrefab] Converting '{prefabPath}' with the Unity project importer. " +
                    "Recognized Poiyomi materials will use the XRENGINE Uber shader.");
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
                    progress: ReportProgress);

                SceneNode convertedRoot = conversion.RootNode
                    ?? throw new SourceVisualImportException(
                        $"Unity prefab conversion returned no hierarchy for '{prefabPath}'.");
                SerializedPrefabImportManifest manifest = conversion.Manifest
                    ?? throw new SourceVisualImportException(
                        $"Unity prefab conversion returned no manifest for '{prefabPath}'.");
                ValidateSerializedPrefabCompletion(manifest, settings, prefabPath);

                (int modelCount, int materialCount, int sourceToonCount, int downgradeCount) =
                    ValidateSerializedPrefabMaterials(convertedRoot, prefabPath);
                SceneNode importRoot = ApplySerializedPrefabImportTransform(convertedRoot, settings);
                int layer = settings.Kind is UnitTestModelImportKind.Static
                    ? DefaultLayers.StaticIndex
                    : DefaultLayers.DynamicIndex;
                importRoot.IterateHierarchy(node => node.Layer = layer);

                result = new SerializedPrefabStartupImportResult(
                    importRoot,
                    modelCount,
                    materialCount,
                    sourceToonCount,
                    downgradeCount,
                    manifest.ComputeSourceContentSha256());
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

        private static void StageSerializedPrefabModelPublication(
            SceneNode rootNode,
            List<ModelComponent> suspendedModels)
        {
            const int MaxModelsPerAppFrame = 2;
            if (suspendedModels.Count == 0)
                return;

            int nextModelIndex = 0;
            Debug.Meshes(
                $"[UnityPrefab] Staging {suspendedModels.Count} model components under " +
                $"'{rootNode.Name}' at up to {MaxModelsPerAppFrame} per app frame.");
            Engine.AddAppThreadCoroutine(() =>
            {
                if (rootNode.IsDestroyed)
                    return true;

                int endIndex = Math.Min(
                    suspendedModels.Count,
                    nextModelIndex + MaxModelsPerAppFrame);
                for (; nextModelIndex < endIndex; nextModelIndex++)
                {
                    ModelComponent component = suspendedModels[nextModelIndex];
                    if (!component.IsDestroyed)
                        component.IsActive = true;
                }

                if (nextModelIndex < suspendedModels.Count)
                    return false;

                Debug.Meshes(
                    $"[UnityPrefab] Finished staged model publication for '{rootNode.Name}'.");
                return true;
            });
        }

        private static void ValidateSerializedPrefabCompletion(
            SerializedPrefabImportManifest manifest,
            Settings.ModelImportSettings settings,
            string prefabPath)
        {
            if (settings.Kind is not UnitTestModelImportKind.Animated ||
                manifest.CompletionTier == SourceImportCompletionTier.VisualAndAvatarBehavior)
            {
                return;
            }

            string[] errors =
            [
                .. manifest.Diagnostics
                    .Where(static diagnostic => diagnostic.Severity == SourceImportDiagnosticSeverity.Error)
                    .Select(static diagnostic => diagnostic.ToString()),
            ];
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
        /// Verifies that every material identified as Poiyomi reached the native Uber material
        /// path. Unsupported Poiyomi versions and generic fallbacks are surfaced as import errors
        /// instead of silently displaying a materially different avatar.
        /// </summary>
        private static (int ModelCount, int MaterialCount, int PoiyomiCount, int DowngradeCount)
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
                    !report.SourceShaderFamily.StartsWith("Poiyomi", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sourceToonCount++;
                bool unsupportedVersion = report.DiagnosticGroups
                    .SelectMany(static group => group.Diagnostics)
                    .Any(static diagnostic => diagnostic.Code == MaterialConversionDiagnosticCodes.UnknownVersion);
                if (unsupportedVersion)
                {
                    throw new SourceVisualImportException(
                        $"Poiyomi material '{report.MaterialName}' in '{prefabPath}' uses an unsupported " +
                        $"shader version ({report.SourceShaderVersion}); the pinned converter target is " +
                        $"{SourceToon93Catalog.VersionText}.");
                }

                if (report.Outcome is EMaterialConversionOutcome.GenericFallback or EMaterialConversionOutcome.Failed)
                {
                    throw new SourceVisualImportException(
                        $"Poiyomi material '{report.MaterialName}' in '{prefabPath}' did not convert to the " +
                        $"native Uber shader (outcome: {report.Outcome}).");
                }

                if (!material.TryGetUberMaterialState(out _, out _))
                {
                    throw new SourceVisualImportException(
                        $"Poiyomi material '{report.MaterialName}' in '{prefabPath}' reported a successful " +
                        "conversion but has no native Uber shader state.");
                }

                if (report.Outcome == EMaterialConversionOutcome.ConvertedToSourceToon)
                    downgradeCount++;
            }

            return (models.Count, materials.Count, sourceToonCount, downgradeCount);
        }
    }
}
