using Assimp;
using XREngine.Extensions;
using System.Numerics;
using XREngine;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Components;
using XREngine.Components.Capture;
using XREngine.Components.Lights;
using XREngine.Components.Physics;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Colors;
using XREngine.Data.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using System.Diagnostics;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static class Models
    {
        private static ModelImporter.DelMakeMaterialAction ResolveMakeMaterialAction(Settings.ModelImportSettings model)
        {
            ModelImporter.DelMakeMaterialAction baseAction = model.MaterialMode switch
            {
                ModelImportMaterialMode.Deferred => ModelImporter.MakeMaterialDeferred,
                ModelImportMaterialMode.Forward => ModelImporter.MakeMaterialForwardPlusTextured,
                ModelImportMaterialMode.Uber => ModelImporter.MakeMaterialForwardPlusUberShader,
                _ => ModelImporter.MakeMaterialDeferred,
            };

            // When deferred mode is active and the user wants forward rendering for transparent
            // textures, wrap the factory so that materials with alpha fall back to the lit forward path.
            if (model.MaterialMode == ModelImportMaterialMode.Deferred && model.UseForwardForTransparent)
            {
                return (textureList, textures, name) =>
                {
                    bool hasTransparency = ModelImporter.ResolveTransparencyMode(textureList, textures)
                        is not ETransparencyMode.Opaque;
                    return hasTransparency
                        ? ModelImporter.MakeMaterialForwardPlusTextured(textureList, textures, name)
                        : baseAction(textureList, textures, name);
                };
            }

            return baseAction;
        }

        private static Matrix4x4? GetOptionalRootTransformMatrix(Settings.ModelImportSettings model)
        {
            if (model is null)
                return null;

            Matrix4x4 transform = Matrix4x4.Identity;

            if (model.YawPitchRoll is not null)
            {
                // JSON values are degrees; Quaternion APIs expect radians.
                var ypr = model.YawPitchRoll;
                var rot = Quaternion.CreateFromYawPitchRoll(
                    XRMath.DegToRad(ypr.Yaw),
                    XRMath.DegToRad(ypr.Pitch),
                    XRMath.DegToRad(ypr.Roll));

                transform *= Matrix4x4.CreateFromQuaternion(rot);
            }

            if (model.Translation is not null)
            {
                var translation = model.Translation;
                transform *= Matrix4x4.CreateTranslation(translation.X, translation.Y, translation.Z);
            }

            return transform == Matrix4x4.Identity ? null : transform;
        }

        private static FbxImportBackend ResolveFbxBackend(Settings.ModelImportSettings model)
            => model.ImporterBackend switch
            {
                ModelImportBackendPreference.AssimpOnly => FbxImportBackend.Assimp,
                _ => FbxImportBackend.Auto,
            };

        private static GltfImportBackend ResolveGltfBackend(Settings.ModelImportSettings model)
            => model.ImporterBackend switch
            {
                ModelImportBackendPreference.AssimpOnly => GltfImportBackend.Assimp,
                _ => GltfImportBackend.Auto,
            };

        internal static ModelImportOptions? CreateImportOptions(Settings.ModelImportSettings model, string[] textureLoadDirSearchPaths)
        {
            bool splitSubmeshes = model.SplitSubmeshesIntoSeparateModelComponents
                || (model.Kind is UnitTestModelImportKind.Static && model.GenerateCoacdCollidersPerSubmesh);

            bool alwaysCreateImportOptions = model.Kind is UnitTestModelImportKind.Static;

            bool needsImportOptions = alwaysCreateImportOptions
                || splitSubmeshes
                || model.ImporterBackend is ModelImportBackendPreference.AssimpOnly
                || textureLoadDirSearchPaths.Length > 0;

            if (!needsImportOptions)
                return null;

            return new ModelImportOptions
            {
                LegacyPostProcessSteps = model.ImportFlags,
                ScaleConversion = model.Scale,
                ZUp = model.ZUp,
                FbxBackend = ResolveFbxBackend(model),
                GltfBackend = ResolveGltfBackend(model),
                GenerateMeshRenderersAsync = true,
                SplitSubmeshesIntoSeparateModelComponents = splitSubmeshes,
                // Keep static models hidden until every submesh has finished CPU-side processing.
                // Dependent systems such as light-probe model bounds should only see complete geometry.
                BatchSubmeshAddsDuringAsyncImport = true,
                TextureLoadDirSearchPaths = textureLoadDirSearchPaths,
            };
        }

        private static string[] ResolveTextureLoadDirSearchPaths()
        {
            if (Toggles.TextureLoadDirSearchPaths.Count == 0)
                return [];

            HashSet<string> resolvedPaths = new(StringComparer.OrdinalIgnoreCase);
            foreach (string rawPath in Toggles.TextureLoadDirSearchPaths)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                    continue;

                string candidate = Path.IsPathRooted(rawPath)
                    ? rawPath
                    : Path.Combine(Environment.CurrentDirectory, rawPath);

                string resolvedPath;
                try
                {
                    resolvedPath = Path.GetFullPath(candidate);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UnitTestingWorld] Ignoring invalid texture search path '{rawPath}'. {ex.Message}");
                    continue;
                }

                if (!Directory.Exists(resolvedPath))
                {
                    Debug.LogWarning($"[UnitTestingWorld] Texture search path does not exist: '{resolvedPath}' (raw='{rawPath}')");
                    continue;
                }

                resolvedPaths.Add(resolvedPath);
            }

            return [.. resolvedPaths];
        }

        private static void AttachAutoGeneratedStaticColliders(SceneNode rootNode)
        {
            int colliderCount = 0;
            rootNode.IterateComponents<ModelComponent>(component =>
            {
                // When multiple ModelComponents exist on one node (split import), each
                // StaticRigidBodyComponent must target its specific ModelComponent.
                var staticBody = component.SceneNode.AddComponent<StaticRigidBodyComponent>();
                if (staticBody is null)
                    return;

                staticBody.TargetModelComponent = component;
                staticBody.AutoGenerateConvexCollidersFromSiblingModel = true;
                colliderCount++;
            }, true);

            Debug.Out($"[StaticModel] Enabled auto-generated CoACD colliders for {colliderCount} imported model components under '{rootNode.Name}'.");
        }

        public static void ImportModels(string desktopDir, SceneNode rootNode, SceneNode characterParentNode)
        {
            if (Toggles.CreateUnitBox)
            {
                SceneNode boxNode = new(rootNode) { Name = "UnitBox" };
                ModelComponent boxComp = boxNode.AddComponent<ModelComponent>()!;
                var mesh = XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f), true, XRMesh.Shapes.ECubemapTextureUVs.None);
                var material = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Red);
                material.RenderOptions.CullMode = ECullMode.None;
                material.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
                boxComp.Model = new Model([new SubMesh(mesh, material)
                {
                    CullingBounds = 
                        new AABB(new Vector3(-0.5f),
                        new Vector3(0.5f)),
                }]);
            }

            if (!Toggles.HasAnyModelsToImport)
                return;

            SceneNode? importedStaticModelsRootNode = null;
            object importedStaticRootsLock = new();
            var importedStaticRoots = new List<SceneNode>();
            string[] textureLoadDirSearchPaths = ResolveTextureLoadDirSearchPaths();
            var modelsToSchedule = new List<Settings.ModelImportSettings>();

            int pendingStaticImports = 0;
            foreach (var m in Toggles.ModelsToImport)
            {
                if (m is null || !m.Enabled || string.IsNullOrWhiteSpace(m.Path))
                    continue;

                modelsToSchedule.Add(m);
                if (m.Kind is UnitTestModelImportKind.Static)
                    pendingStaticImports++;
            }

            if (modelsToSchedule.Count == 0)
                return;

            int staticImportCompletionQueued = 0;
            void CompleteStaticImportSlot()
            {
                if (Interlocked.Decrement(ref pendingStaticImports) != 0)
                    return;

                if (Interlocked.CompareExchange(ref staticImportCompletionQueued, 1, 0) != 0)
                    return;

                QueueLightProbeSpawnerModelBoundsWiring(rootNode, importedStaticRootsLock, importedStaticRoots);
            }

            void ScheduleModelImport(Settings.ModelImportSettings model)
            {
                string resolvedPath = ResolveModelPath(desktopDir, model.Path);
                if (!File.Exists(resolvedPath))
                {
                    Debug.LogWarning($"[UnitTestingWorld] Model file not found at '{resolvedPath}' (raw='{model.Path}')");
                    if (model.Kind is UnitTestModelImportKind.Static)
                        CompleteStaticImportSlot();
                    return;
                }

                switch (model.Kind)
                {
                    case UnitTestModelImportKind.Animated:
                    {
                        ModelImporter.DelMakeMaterialAction makeMaterialAction = ResolveMakeMaterialAction(model);
                        ModelImportOptions? importOptions = CreateImportOptions(model, textureLoadDirSearchPaths);

                        _ = ModelImporter.ScheduleImportJob(
                            resolvedPath,
                            model.ImportFlags,
                            onFinished: result =>
                            {
                                _ = Engine.InvokeOnAppThread(() =>
                                {
                                    SceneNode? importedNode = result.RootNode;
                                    if (characterParentNode != null && importedNode != null && importedNode.Transform.Parent != characterParentNode.Transform)
                                        characterParentNode.Transform.AddChild(importedNode.Transform, false, EParentAssignmentMode.Immediate);

                                    OnFinishedImportingAvatar(importedNode, characterParentNode);
                                }, $"UnitTestingWorld: Finish animated avatar import '{Path.GetFileName(resolvedPath)}'", executeNowIfAlreadyAppThread: true);
                            },
                            onError: ex => Debug.LogException(ex, $"[AnimatedModel] Failed to import animated model: {resolvedPath}"),
                            onCanceled: () => Debug.LogWarning($"[AnimatedModel] Import was canceled: {resolvedPath}"),
                            onProgress: progress => Debug.Out($"[AnimatedModel] Progress ({Path.GetFileName(resolvedPath)}): {progress:P0}"),
                            cancellationToken: default,
                            parent: null,
                            scaleConversion: model.Scale,
                            zUp: model.ZUp,
                            rootTransformMatrix: GetOptionalRootTransformMatrix(model),
                            materialFactory: null,
                            makeMaterialAction: makeMaterialAction,
                            importOptions: importOptions,
                            layer: DefaultLayers.DynamicIndex);
                        break;
                    }
                    case UnitTestModelImportKind.Static:
                    default:
                    {
                        importedStaticModelsRootNode ??= new SceneNode(rootNode) { Name = "Static Model Root", Layer = DefaultLayers.StaticIndex };
                        ModelImporter.DelMakeMaterialAction makeMaterialAction = ResolveMakeMaterialAction(model);
                        ModelImportOptions? importOptions = CreateImportOptions(model, textureLoadDirSearchPaths);

                        // Capture the root node ref for the closure below.
                        SceneNode staticRoot = importedStaticModelsRootNode;

                        _ = ModelImporter.ScheduleImportJob(
                            resolvedPath,
                            model.ImportFlags,
                            onFinished: result =>
                            {
                                _ = Engine.InvokeOnAppThread(() =>
                                {
                                    if (result.RootNode is null)
                                        Debug.LogWarning($"[StaticModel] Import finished but RootNode is null: {resolvedPath}");
                                    else
                                    {
                                        if (model.GenerateCoacdCollidersPerSubmesh)
                                            AttachAutoGeneratedStaticColliders(result.RootNode);
                                        lock (importedStaticRootsLock)
                                            importedStaticRoots.Add(result.RootNode);
                                        Debug.Out($"[StaticModel] Import completed: {resolvedPath} ({result.Meshes.Count} meshes, {result.Materials.Count} materials)");
                                    }

                                    CompleteStaticImportSlot();
                                }, $"UnitTestingWorld: Finish static model import '{Path.GetFileName(resolvedPath)}'", executeNowIfAlreadyAppThread: true);
                            },
                            onError: ex =>
                            {
                                Debug.LogException(ex, $"[StaticModel] Failed to import static model: {resolvedPath}");
                                CompleteStaticImportSlot();
                            },
                            onCanceled: () =>
                            {
                                Debug.LogWarning($"[StaticModel] Import was canceled: {resolvedPath}");
                                CompleteStaticImportSlot();
                            },
                            onProgress: progress => Debug.Out($"[StaticModel] Progress ({Path.GetFileName(resolvedPath)}): {progress:P0}"),
                            cancellationToken: default,
                            parent: importedStaticModelsRootNode,
                            scaleConversion: model.Scale,
                            zUp: model.ZUp,
                            rootTransformMatrix: GetOptionalRootTransformMatrix(model),
                            materialFactory: null,
                            makeMaterialAction: makeMaterialAction,
                            importOptions: importOptions,
                            layer: DefaultLayers.StaticIndex);

                        break;
                    }
                }
            }

            int nextModelIndex = 0;
            if (Engine.StartingUp)
                Debug.Out($"[UnitTestingWorld] Scheduling {modelsToSchedule.Count} startup model import(s) across app-thread dispatches.");

            Engine.AddAppThreadCoroutine(() =>
            {
                if (nextModelIndex >= modelsToSchedule.Count)
                    return true;

                ScheduleModelImport(modelsToSchedule[nextModelIndex++]);
                return nextModelIndex >= modelsToSchedule.Count;
            });
        }

        private static void QueueLightProbeSpawnerModelBoundsWiring(SceneNode rootNode, object importedStaticRootsLock, List<SceneNode> importedStaticRoots)
        {
            Engine.InvokeOnAppThread(() =>
            {
                SceneNode[] importedRoots;
                lock (importedStaticRootsLock)
                    importedRoots = [.. importedStaticRoots];

                WireLightProbeSpawnerToImportedModels(rootNode, importedRoots);
            }, "UnitTestingWorld.Models.QueueLightProbeSpawnerModelBoundsWiring", executeNowIfAlreadyAppThread: true);
        }

        /// <summary>
        /// Called once all static model imports have completed. Finds the light probe grid spawner
        /// in the scene tree and sets its <see cref="LightProbeGridSpawnerComponent.PlacementBoundsModels"/>
        /// to the imported <see cref="ModelComponent"/> instances so probes fill the models' combined volume.
        /// </summary>
        private static void WireLightProbeSpawnerToImportedModels(SceneNode rootNode, SceneNode[] importedStaticRoots)
        {
            var spawners = rootNode.FindAllDescendantComponents<LightProbeGridSpawnerComponent>();
            if (spawners.Length == 0)
            {
                Debug.Out("[LightProbeGrid] No LightProbeGridSpawnerComponent found � skipping model-bounds wiring.");
                return;
            }

            var models = new List<ModelComponent>();
            foreach (SceneNode importedRoot in importedStaticRoots)
                importedRoot.IterateComponents<ModelComponent>(m => models.Add(m), iterateChildHierarchy: true);

            if (models.Count == 0)
            {
                Debug.Out("[LightProbeGrid] No ModelComponents found under imported static models � skipping model-bounds wiring.");
                return;
            }

            ModelComponent[] modelsArray = [.. models];
            foreach (var spawner in spawners)
            {
                if (!spawner.UsePlacementBoundsModels)
                    continue;

                spawner.ConfigurePlacementBoundsModels(modelsArray, enabled: true);
                Debug.Out($"[LightProbeGrid] Wired {modelsArray.Length} imported ModelComponents as placement bounds for '{spawner.Name}'.");
            }
        }

        private static string ResolveModelPath(string desktopDir, string rawPath)
        {
            if (Path.IsPathRooted(rawPath))
                return rawPath;

            string candidate = Path.Combine(desktopDir, rawPath);
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(Engine.Assets.EngineAssetsPath, rawPath);
            if (File.Exists(candidate))
                return candidate;

            // Convenience for the common "Models/..." layout under engine assets.
            if (!rawPath.StartsWith("Models\\", StringComparison.OrdinalIgnoreCase) &&
                !rawPath.StartsWith("Models/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.Combine(Engine.Assets.EngineAssetsPath, "Models", rawPath);
                if (File.Exists(candidate))
                    return candidate;
            }

            return candidate;
        }

        private static string ResolveUnitTestAssetPath(string desktopDir, string rawPath)
        {
            if (Path.IsPathRooted(rawPath))
                return rawPath;

            rawPath = rawPath.Replace('/', Path.DirectorySeparatorChar);
            string cwd = Environment.CurrentDirectory;

            // Mirrors how Rive loads assets: treat names as relative to the current working directory.
            // The editor is usually launched with cwd = 'XREngine.Editor' (see Start-Editor.bat).
            string candidate = Path.Combine(cwd, rawPath);
            if (File.Exists(candidate))
                return candidate;

            // Support repo-relative paths like 'XREngine.Editor\\Assets\\...'
            // when cwd already *is* 'XREngine.Editor'.
            if (string.Equals(Path.GetFileName(cwd), "XREngine.Editor", StringComparison.OrdinalIgnoreCase))
            {
                const string prefix = "XREngine.Editor\\";
                if (rawPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = Path.Combine(cwd, rawPath[prefix.Length..]);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            // Common caller convenience: allow paths relative to the editor's Assets folder.
            candidate = Path.Combine(cwd, "Assets", rawPath);
            if (File.Exists(candidate))
                return candidate;

            // Also try repo root (parent of XREngine.Editor) for repo-relative paths.
            var parent = Directory.GetParent(cwd);
            if (parent is not null)
            {
                candidate = Path.Combine(parent.FullName, rawPath);
                if (File.Exists(candidate))
                    return candidate;

                candidate = Path.Combine(parent.FullName, "XREngine.Editor", rawPath);
                if (File.Exists(candidate))
                    return candidate;
            }

            candidate = Path.Combine(desktopDir, rawPath);
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(Engine.Assets.GameAssetsPath, rawPath);
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(Engine.Assets.EngineAssetsPath, rawPath);
            if (File.Exists(candidate))
                return candidate;

            return Path.GetFullPath(candidate);
        }

        private static string ResolveUnitTestArtifactPath(string rawPath)
        {
            if (Path.IsPathRooted(rawPath))
                return rawPath;

            rawPath = rawPath.Replace('/', Path.DirectorySeparatorChar);
            string cwd = Environment.CurrentDirectory;
            string baseDir = string.Equals(Path.GetFileName(cwd), "XREngine.Editor", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(cwd)?.FullName ?? cwd
                : cwd;

            return Path.GetFullPath(Path.Combine(baseDir, rawPath));
        }

        public static SkyboxComponent? AddSkybox(
            SceneNode rootNode,
            XRTexture2D? skyEquirect,
            bool useProceduralSky = false,
            DirectionalLightComponent? sunDirectionalLight = null,
            DirectionalLightComponent? moonDirectionalLight = null)
        {
            var skybox = new SceneNode(rootNode) { Name = "TestSkyboxNode" };
            if (!skybox.TryAddComponent<SkyboxComponent>(out var skyboxComp))
                return null;

            skyboxComp!.Name = "TestSkybox";
            skyboxComp.Intensity = 1.0f;

            if (useProceduralSky)
            {
                skyboxComp.Mode = ESkyboxMode.DynamicProcedural;
                skyboxComp.SunDirectionalLight = sunDirectionalLight;
                skyboxComp.SyncDirectionalLightWithSun = sunDirectionalLight is not null;
                skyboxComp.MoonDirectionalLight = moonDirectionalLight;
                skyboxComp.SyncDirectionalLightWithMoon = moonDirectionalLight is not null;
            }
            else if (skyEquirect is null)
            {
                skyboxComp.Mode = ESkyboxMode.Gradient;
            }
            else
            {
                skyboxComp.Mode = ESkyboxMode.Texture;
                skyboxComp.Projection = ESkyboxProjection.Equirectangular;
                skyboxComp.Texture = skyEquirect;
            }

            return skyboxComp;
        }

        private static void OnFinishedAvatarAsync(Task<(SceneNode? rootNode, IReadOnlyCollection<XRMaterial> materials, IReadOnlyCollection<XRMesh> meshes)> task)
        {
            if (task.IsCanceled || task.IsFaulted)
                return;

            (SceneNode? rootNode, IReadOnlyCollection<XRMaterial> materials, IReadOnlyCollection<XRMesh> meshes) = task.Result;
            OnFinishedImportingAvatar(rootNode);
        }

        private sealed class AvatarStartupClipLoadResult
        {
            public AnimationClip? VmdClip { get; init; }
            public AnimationClip? AnimClip { get; init; }
            public string? AnimClipPath { get; init; }
            public bool AnimClipFileExists { get; init; }
            public bool RunPoseAudit { get; init; }
        }

        public static void OnFinishedImportingAvatar(SceneNode? rootNode)
            => OnFinishedImportingAvatar(rootNode, characterParentNode: null);

        private static async Task<AnimationClip?> LoadStartupAnimationClipOnWorkerAsync(string clipPath, string label)
        {
            // Run startup loads on a worker and keep nested asset work inline so the app thread
            // does not block behind a scheduled load job during startup.
            return await Task.Run(() =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                Debug.Out($"[UnitTestingWorld] Starting startup {label} clip load from '{clipPath}'.");

                AnimationClip? clip = File.Exists(clipPath)
                    ? Engine.Assets.Load<AnimationClip>(clipPath, bypassJobThread: true)
                    : null;

                stopwatch.Stop();
                Debug.Out($"[UnitTestingWorld] Startup {label} clip load finished in {stopwatch.Elapsed.TotalMilliseconds:F1} ms. Loaded={(clip is not null)} Path='{clipPath}'.");
                return clip;
            }).ConfigureAwait(false);
        }

        private static async Task<AvatarStartupClipLoadResult?> LoadAvatarStartupClipsAsync()
        {
            if (!Toggles.AnimationClipVMD && !Toggles.AnimationClipAnim)
                return null;

            Stopwatch startupClipLoadStopwatch = Stopwatch.StartNew();
            Debug.Out($"[UnitTestingWorld] Begin startup clip load. AnimationClipVMD={Toggles.AnimationClipVMD} AnimationClipAnim={Toggles.AnimationClipAnim} RawAnimPath='{Toggles.AnimClipPath}'.");

            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            Task<AnimationClip?>? vmdClipTask = null;
            if (Toggles.AnimationClipVMD)
            {
                string vmdClipPath = Path.Combine(desktopDir, "test.vmd");
                vmdClipTask = LoadStartupAnimationClipOnWorkerAsync(vmdClipPath, "VMD");
            }

            string? animClipPath = null;
            bool animClipFileExists = false;
            Task<AnimationClip?>? animClipTask = null;
            if (Toggles.AnimationClipAnim)
            {
                animClipPath = ResolveUnitTestAssetPath(desktopDir, Toggles.AnimClipPath);
                animClipFileExists = File.Exists(animClipPath);
                Debug.Out($"[UnitTestingWorld] Resolved startup .anim clip path to '{animClipPath}'. Exists={animClipFileExists}.");
                if (animClipFileExists)
                    animClipTask = LoadStartupAnimationClipOnWorkerAsync(animClipPath, ".anim");
            }

            AnimationClip? vmdClip = vmdClipTask is null
                ? null
                : await vmdClipTask.ConfigureAwait(false);

            AnimationClip? animClip = animClipTask is null
                ? null
                : await animClipTask.ConfigureAwait(false);

            startupClipLoadStopwatch.Stop();
            Debug.Out($"[UnitTestingWorld] Startup clip load completed in {startupClipLoadStopwatch.Elapsed.TotalMilliseconds:F1} ms. VMDLoaded={(vmdClip is not null)} AnimLoaded={(animClip is not null)} AnimExists={animClipFileExists} AnimPath='{animClipPath ?? "<null>"}'.");

            return new AvatarStartupClipLoadResult
            {
                VmdClip = vmdClip,
                AnimClip = animClip,
                AnimClipPath = animClipPath,
                AnimClipFileExists = animClipFileExists,
                RunPoseAudit = Toggles.HumanoidPoseAuditEnabled || !string.IsNullOrWhiteSpace(Toggles.HumanoidPoseAuditReferencePath),
            };
        }

        public static void OnFinishedImportingAvatar(SceneNode? rootNode, SceneNode? characterParentNode)
            => OnFinishedImportingAvatar(rootNode, characterParentNode, startupClips: null);

        private static void OnFinishedImportingAvatar(SceneNode? rootNode, SceneNode? characterParentNode, AvatarStartupClipLoadResult? startupClips)
        {
            if (rootNode is null)
                return;

            QueueAvatarPostImportSetup(rootNode, characterParentNode, startupClips);
        }

        private static void QueueAvatarPostImportSetup(SceneNode rootNode, SceneNode? characterParentNode, AvatarStartupClipLoadResult? startupClips)
        {
            HumanoidComponent? humanComp = null;
            HeightScaleBaseComponent? heightScale = null;
            VRIKSolverComponent? vrIKSolver = null;
            int stage = 0;

            // Spread startup-only avatar setup across app-thread dispatches so input and camera
            // updates keep getting time slices while heavy humanoid/clip initialization runs.
            Engine.AddAppThreadCoroutine(() =>
            {
                if (rootNode.IsDestroyed)
                    return true;

                switch (stage)
                {
                    case 0:
                        using (HumanoidComponent.BeginDeferredSceneNodeInitialization())
                            humanComp = rootNode.AddComponent<HumanoidComponent>()!;

                        stage++;
                        return false;

                    case 1:
                        if (humanComp is null || humanComp.IsDestroyed)
                            return true;

                        humanComp.InitializeSceneNodeBindings();
                        stage++;
                        return false;

                    case 2:
                        if (humanComp is null || humanComp.IsDestroyed)
                            return true;

                        var animator = rootNode.AddComponent<AnimStateMachineComponent>()!;

                        // VRHeightScaleComponent depends on VRState. For desktop locomotion we just scale once to fit the capsule.
                        if (Toggles.VRPawn)
                            heightScale = rootNode.AddComponent<VRHeightScaleComponent>()!;
                        else
                        {
                            var desktopHeightScale = rootNode.AddComponent<HeightScaleComponent>()!;
                            heightScale = desktopHeightScale;
                            TryScaleAvatarToFitDesktopCapsule(rootNode, humanComp, desktopHeightScale, characterParentNode);
                        }

                        if (Toggles.FaceTracking)
                            ConfigureAvatarFaceTracking(rootNode, animator);

                        stage++;
                        return false;

                    case 3:
                        if (humanComp is null || humanComp.IsDestroyed)
                            return true;

                        vrIKSolver = ConfigureAvatarCharacterIK(rootNode, humanComp);
                        stage++;
                        return false;

                    case 4:
                        if (humanComp is null || humanComp.IsDestroyed)
                            return true;

                        if (Toggles.Locomotion)
                        {
                            if (heightScale is null)
                                return true;

                            Pawns.InitializeLocomotion(rootNode, humanComp, heightScale, vrIKSolver);
                        }

                        if (Toggles.TestAnimation)
                            ConfigureAvatarTestAnimation(rootNode, humanComp);

                        if (Toggles.PhysicsChain)
                            CreateHardcodedPhysicsChains(humanComp);

                        stage++;
                        return false;

                    case 5:
                        if (humanComp is null || humanComp.IsDestroyed)
                            return true;

                        ConfigureAvatarPostSetupFeatures(rootNode, humanComp);

                        if (startupClips is not null)
                            AttachAvatarStartupClips(rootNode, humanComp, startupClips);
                        else
                            QueueAvatarStartupClipAttachment(rootNode);

                        return true;

                    default:
                        return true;
                }
            });
        }

        private static void ConfigureAvatarFaceTracking(SceneNode rootNode, AnimStateMachineComponent animator)
        {
            const string vrcftPrefix = "/avatar/parameters/";
            var ftOscReceiver = rootNode.AddComponent<FaceTrackingReceiverComponent>()!;
            ftOscReceiver.ParameterPrefix = vrcftPrefix;
            ftOscReceiver.GenerateARKitStateMachine();

            var ftOscSender = rootNode.AddComponent<OscSenderComponent>()!;
            ftOscSender.ParameterPrefix = vrcftPrefix;

            animator.StateMachine.VariableChanged += StateMachineVariableChanged;

            void StateMachineVariableChanged(AnimVar variable)
            {
                string address = variable.ParameterName;
                switch (variable)
                {
                    case AnimFloat f:
                        ftOscSender.Send(address, f.Value);
                        break;
                    case AnimInt i:
                        ftOscSender.Send(address, i.Value);
                        break;
                    case AnimBool b:
                        ftOscSender.Send(address, b.Value);
                        break;
                }
            }
        }

        private static VRIKSolverComponent? ConfigureAvatarCharacterIK(SceneNode rootNode, HumanoidComponent humanComp)
        {
            if (!Toggles.AddCharacterIK)
                return null;

            if (!Toggles.VRPawn)
            {
                var humanik = rootNode.AddComponent<HumanoidIKSolverComponent>()!;

                humanik.SetIKPositionWeight(ELimbEndEffector.LeftHand, 1.0f);
                humanik.SetIKRotationWeight(ELimbEndEffector.LeftHand, 1.0f);

                humanik.SetIKPositionWeight(ELimbEndEffector.RightHand, 1.0f);
                humanik.SetIKRotationWeight(ELimbEndEffector.RightHand, 1.0f);

                humanik.SetIKPositionWeight(ELimbEndEffector.LeftFoot, 1.0f);
                humanik.SetIKRotationWeight(ELimbEndEffector.LeftFoot, 1.0f);

                humanik.SetIKPositionWeight(ELimbEndEffector.RightFoot, 1.0f);
                humanik.SetIKRotationWeight(ELimbEndEffector.RightFoot, 1.0f);

                humanik.SetSpineWeight(0.0f);

                SceneNode leftFootIkTargetNode = rootNode.NewChild("LeftFootIKTargetNode");
                var leftFootTfm = humanComp.Left.Foot.Node!.GetTransformAs<Transform>(true)!;
                leftFootIkTargetNode.GetTransformAs<Transform>(true)!.SetFrameState(new TransformState()
                {
                    Order = ETransformOrder.TRS,
                    Rotation = leftFootTfm.WorldRotation,
                    Scale = new Vector3(1.0f),
                    Translation = leftFootTfm.WorldTranslation
                });

                SceneNode rightFootIkTargetNode = rootNode.NewChild("RightFootIKTargetNode");
                var rightFootTfm = humanComp.Right.Foot.Node!.GetTransformAs<Transform>(true)!;
                rightFootIkTargetNode.GetTransformAs<Transform>(true)!.SetFrameState(new TransformState()
                {
                    Order = ETransformOrder.TRS,
                    Rotation = rightFootTfm.WorldRotation,
                    Scale = new Vector3(1.0f),
                    Translation = rightFootTfm.WorldTranslation
                });

                SceneNode leftHandIkTargetNode = rootNode.NewChild("LeftHandIKTargetNode");
                var leftHandTfm = humanComp.Left.Wrist.Node!.GetTransformAs<Transform>(true)!;
                leftHandIkTargetNode.GetTransformAs<Transform>(true)!.SetFrameState(new TransformState()
                {
                    Order = ETransformOrder.TRS,
                    Rotation = leftHandTfm.WorldRotation,
                    Scale = new Vector3(1.0f),
                    Translation = leftHandTfm.WorldTranslation
                });

                SceneNode rightHandIkTargetNode = rootNode.NewChild("RightHandIKTargetNode");
                var rightHandTfm = humanComp.Right.Wrist.Node!.GetTransformAs<Transform>(true)!;
                rightHandIkTargetNode.GetTransformAs<Transform>(true)!.SetFrameState(new TransformState()
                {
                    Order = ETransformOrder.TRS,
                    Rotation = rightHandTfm.WorldRotation,
                    Scale = new Vector3(1.0f),
                    Translation = rightHandTfm.WorldTranslation
                });

                humanik.GetGoalIK(ELimbEndEffector.LeftFoot)!.TargetIKTransform = leftFootIkTargetNode.Transform;
                humanik.GetGoalIK(ELimbEndEffector.RightFoot)!.TargetIKTransform = rightFootIkTargetNode.Transform;
                humanik.GetGoalIK(ELimbEndEffector.LeftHand)!.TargetIKTransform = leftHandIkTargetNode.Transform;
                humanik.GetGoalIK(ELimbEndEffector.RightHand)!.TargetIKTransform = rightHandIkTargetNode.Transform;
                UserInterface.EnableTransformToolForNode(leftFootIkTargetNode);
                UserInterface.EnableTransformToolForNode(rightFootIkTargetNode);
                UserInterface.EnableTransformToolForNode(leftHandIkTargetNode);
                UserInterface.EnableTransformToolForNode(rightHandIkTargetNode);
                Selection.SceneNode = leftFootIkTargetNode;
                return null;
            }

            var vrik = rootNode.AddComponent<VRIKSolverComponent>()!;
            vrik.IsActive = false;
            return vrik;
        }

        private static void ConfigureAvatarTestAnimation(SceneNode rootNode, HumanoidComponent humanComp)
        {
            var knee = humanComp.Right.Knee?.Node?.Transform;
            var leg = humanComp.Right.Leg?.Node?.Transform;

            leg?.RegisterAnimationTick<Transform>(t => t.Rotation = Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(180 - 90.0f * (MathF.Cos(Engine.ElapsedTime) * 0.5f + 0.5f))));
            knee?.RegisterAnimationTick<Transform>(t => t.Rotation = Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(90.0f * (MathF.Cos(Engine.ElapsedTime) * 0.5f + 0.5f))));

            //For testing blendshape morphing
            //Technically we should only register animation tick on scene node if any lods have blendshapes,
            //but at this point the model's meshes are still loading. So we'll just be lazy and check in the animation tick since it's just for testing.
            rootNode.IterateComponents<ModelComponent>(comp =>
            {
                comp.SceneNode.RegisterAnimationTick<SceneNode>(t =>
                {
                    var renderers = comp.Meshes.SelectMany(x => x.LODs).Select(x => x.Renderer).Where(x => x?.Mesh?.HasBlendshapes ?? false);
                    foreach (var renderer in renderers)
                    {
                        int r = 0;
                        renderer?.SetBlendshapeWeightNormalized((uint)r, MathF.Sin(Engine.ElapsedTime) * 0.5f + 0.5f);
                    }
                });
            }, true);
        }

        private static void ConfigureAvatarPostSetupFeatures(SceneNode rootNode, HumanoidComponent humanComp)
        {
            if (Toggles.TransformTool && humanComp.SceneNode is { } root)
                UserInterface.EnableTransformToolForNode(root);

            if (Toggles.VMC)
            {
                var vmc = rootNode.AddComponent<VMCCaptureComponent>()!;
                vmc.Humanoid = humanComp;
            }

            if (Toggles.FaceMotion3D)
            {
                var faceMotion = rootNode.AddComponent<FaceMotion3DCaptureComponent>()!;
                faceMotion.Humanoid = humanComp;
                var glasses = humanComp.SceneNode?.FindDescendant(x => x.Name?.Contains("glasses", StringComparison.InvariantCultureIgnoreCase) ?? false);
                glasses?.IsActiveSelf = false;
            }

            OVRLipSyncComponent? lipSync = null;
            if (Toggles.AttachMicToAnimatedModel)
            {
                var headNode = humanComp.Head?.Node?.Transform?.SceneNode;
                if (headNode is not null)
                    Audio.AttachMicTo(headNode, out _, out _, out lipSync);
            }
            else if (Toggles.LipSync)
                lipSync = (Engine.State.MainPlayer?.ControlledPawnComponent as PawnComponent)?.SceneNode?.GetComponent<OVRLipSyncComponent>();

            if (lipSync is null)
                return;

            var face = rootNode.FindDescendantByName("Face", StringComparison.InvariantCultureIgnoreCase);
            if (face is null)
                return;

            if (face.TryGetComponent<ModelComponent>(out var comp))
            {
                lipSync.ModelComponent = comp;
                return;
            }

            face.ComponentAdded += SetModel;

            void SetModel((SceneNode node, XRComponent comp) x)
            {
                if (x.comp is ModelComponent model)
                    lipSync.ModelComponent = model;

                face.ComponentAdded -= SetModel;
            }
        }

        private static void QueueAvatarStartupClipAttachment(SceneNode rootNode)
        {
            if (!Toggles.AnimationClipVMD && !Toggles.AnimationClipAnim)
                return;

            _ = LoadAndAttachAsync();

            async Task LoadAndAttachAsync()
            {
                try
                {
                    Stopwatch startupClipAttachmentStopwatch = Stopwatch.StartNew();
                    Debug.Out($"[UnitTestingWorld] Starting startup clip attachment flow for '{rootNode.Name}'.");

                    AvatarStartupClipLoadResult? startupClips = await LoadAvatarStartupClipsAsync().ConfigureAwait(false);
                    if (startupClips is null)
                    {
                        startupClipAttachmentStopwatch.Stop();
                        Debug.Out($"[UnitTestingWorld] Startup clip attachment flow for '{rootNode.Name}' exited early after {startupClipAttachmentStopwatch.Elapsed.TotalMilliseconds:F1} ms because no startup clip toggles were enabled.");
                        return;
                    }

                    startupClipAttachmentStopwatch.Stop();
                    Debug.Out($"[UnitTestingWorld] Startup clip attachment flow for '{rootNode.Name}' finished loading clip data in {startupClipAttachmentStopwatch.Elapsed.TotalMilliseconds:F1} ms.");

                    _ = Engine.InvokeOnAppThread(() =>
                    {
                        if (!rootNode.TryGetComponent<HumanoidComponent>(out var humanComp) || humanComp is null)
                        {
                            Debug.LogWarning($"[UnitTestingWorld] Skipping startup clip attachment for '{rootNode.Name}' because no HumanoidComponent exists on the imported avatar root.");
                            return;
                        }

                        AttachAvatarStartupClips(rootNode, humanComp, startupClips);
                    }, $"UnitTestingWorld: Attach startup clips to '{rootNode.Name}'", executeNowIfAlreadyAppThread: true);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, "[UnitTestingWorld] Failed to load avatar startup clips.");
                }
            }
        }

        private static void AttachAvatarStartupClips(SceneNode rootNode, HumanoidComponent humanComp, AvatarStartupClipLoadResult startupClips)
        {
            AnimationClipComponent? vmdComponent = null;
            AnimationClipComponent? animClipComponent = null;
            int stage = 0;

            Engine.AddAppThreadCoroutine(() =>
            {
                if (rootNode.IsDestroyed || humanComp.IsDestroyed)
                    return true;

                switch (stage)
                {
                    case 0:
                        if (startupClips.VmdClip is AnimationClip vmdClip)
                        {
                            vmdClip.Looped = true;

                            vmdComponent = rootNode.AddComponent<AnimationClipComponent>()!;
                            vmdComponent.StartOnActivate = false;
                            vmdComponent.Animation = vmdClip;
                        }

                        stage++;
                        return false;

                    case 1:
                        vmdComponent?.PlayDeferred();
                        stage++;
                        return false;

                    case 2:
                        if (!Toggles.AnimationClipAnim)
                            return true;

                        if (!startupClips.AnimClipFileExists)
                        {
                            string resolvedPath = startupClips.AnimClipPath ?? Toggles.AnimClipPath;
                            Debug.LogWarning($"[UnitTestingWorld] .anim clip not found at '{resolvedPath}' (raw='{Toggles.AnimClipPath}')");
                            return true;
                        }

                        if (startupClips.AnimClip is not AnimationClip clip)
                        {
                            string resolvedPath = startupClips.AnimClipPath ?? Toggles.AnimClipPath;
                            Debug.LogWarning($"[UnitTestingWorld] Failed to load .anim clip from '{resolvedPath}' even though the file exists. No AnimationClipComponent was added to '{rootNode.Name}'.");
                            return true;
                        }

                        clip.Looped = Toggles.AnimLooped;

                        bool runPoseAudit = startupClips.RunPoseAudit;

                        animClipComponent = rootNode.AddComponent<AnimationClipComponent>()!;
                        animClipComponent.Name = "Startup .anim Clip";
                        animClipComponent.StartOnActivate = false;
                        animClipComponent.Animation = clip;

                        Debug.Out($"[UnitTestingWorld] Attached startup .anim clip '{clip.Name}' to '{rootNode.Name}'.");

                        if (clip.ClipKind == EAnimationClipKind.UnityHumanoidMuscle && !runPoseAudit)
                        {
                            Debug.LogWarning($"[UnitTestingWorld] Loaded Unity humanoid clip '{clip.Name}' and started playback. Raw .anim humanoid curves still do not fully match Unity HumanPose semantics; enable HumanoidPoseAudit* settings if you need to compare that path precisely.");
                        }

                        if (clip.ClipKind == EAnimationClipKind.UnityHumanoidMuscle
                            && rootNode.TryGetComponent<HumanoidIKSolverComponent>(out var humanoidIK)
                            && humanoidIK is not null)
                        {
                            humanoidIK.IsActive = false;
                            Debug.Out($"[UnitTestingWorld] Disabled HumanoidIKSolverComponent for humanoid clip '{clip.Name}' to inspect raw muscle playback.");
                        }

                        if (runPoseAudit)
                        {
                            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                            var audit = rootNode.AddComponent<HumanoidPoseAuditComponent>()!;
                            audit.TargetClipComponent = animClipComponent;
                            audit.TargetHumanoid = humanComp;
                            audit.OutputPath = ResolveUnitTestArtifactPath(Toggles.HumanoidPoseAuditOutputPath);
                            audit.ReferencePath = string.IsNullOrWhiteSpace(Toggles.HumanoidPoseAuditReferencePath)
                                ? null
                                : ResolveUnitTestAssetPath(desktopDir, Toggles.HumanoidPoseAuditReferencePath);
                            audit.ComparisonOutputPath = string.IsNullOrWhiteSpace(Toggles.HumanoidPoseAuditComparisonOutputPath)
                                ? null
                                : ResolveUnitTestArtifactPath(Toggles.HumanoidPoseAuditComparisonOutputPath);
                            audit.SampleRateOverride = Toggles.HumanoidPoseAuditSampleRateOverride ?? 0;

                            Debug.Out($"[UnitTestingWorld] Enabled humanoid pose audit export for clip '{clip.Name}' -> '{audit.OutputPath}'.");
                        }

                        stage++;
                        return false;

                    case 3:
                        animClipComponent?.PlayDeferred();
                        return true;

                    default:
                        return true;
                }
            });
        }

        private static void TryScaleAvatarToFitDesktopCapsule(
            SceneNode avatarRoot,
            HumanoidComponent humanoid,
            HeightScaleComponent heightScale,
            SceneNode? characterParentNode)
        {
            // Determine the character capsule dimensions from the desktop character movement component.
            var characterNode = characterParentNode?.Parent;
            var movement = characterNode?.GetComponent<CharacterMovement3DComponent>();
            if (movement is null)
                return;

            // Measure model height in bind space using the humanoid head vs root, similar to VRHeightScaleComponent.MeasureAvatarHeight.
            var headNode = humanoid.Head.Node;
            if (headNode is null)
                return;

            float headY = headNode.Transform.BindMatrix.Translation.Y;
            float rootY = humanoid.SceneNode.Transform.BindMatrix.Translation.Y;
            float modelHeight = headY - rootY;
            if (!float.IsFinite(modelHeight) || modelHeight <= 0.001f)
                return;

            float capsuleOuterHeight = movement.CurrentHeight + 2.0f * (movement.Radius + movement.ContactOffset);
            if (!float.IsFinite(capsuleOuterHeight) || capsuleOuterHeight <= 0.001f)
                return;

            // Use the HeightScaleComponent that was just added to apply measured height + a single uniform scale.
            // Keep the desktop character capsule as the source of truth: only scale down (never up) to fit.
            float rawRatio = capsuleOuterHeight / modelHeight;
            if (!float.IsFinite(rawRatio) || rawRatio <= 0.0f)
                return;
            float ratio = MathF.Min(1.0f, rawRatio);

            // Ensure the component has what it needs without letting it resize the character capsule.
            heightScale.HumanoidComponent = humanoid;
            heightScale.CharacterMovementComponent = null;

            heightScale.ApplyMeasuredHeight(modelHeight);
            heightScale.RealWorldHeightRatio = ratio;
        }

        #region Hardcoded stuff, varies per model
        private static void CreateHardcodedPhysicsChains(HumanoidComponent humanComp)
        {
            //Add physics chain to the breast bone
            var chest = humanComp!.Chest?.Node?.Transform;
            //Find breast bone
            if (chest is not null)
            {
                var earR = chest.FindDescendant(x =>
                    (x.Name?.Contains("KittenEarR", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (earR?.SceneNode is not null)
                {
                    var phys = earR.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                var earL = chest.FindDescendant(x =>
                    (x.Name?.Contains("KittenEarL", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (earL?.SceneNode is not null)
                {
                    var phys = earL.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                var breastL = chest.FindDescendant(x =>
                    (x.Name?.Contains("BreastUpper2_LRoot", StringComparison.InvariantCultureIgnoreCase) ?? false) ||
                    (x.Name?.Contains("Boob.L", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (breastL?.SceneNode is not null)
                {
                    var phys = breastL.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                var breastR = chest.FindDescendant(x =>
                    (x.Name?.Contains("BreastUpper2_RRoot", StringComparison.InvariantCultureIgnoreCase) ?? false) ||
                    (x.Name?.Contains("Boob.R", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (breastR?.SceneNode is not null)
                {
                    var phys = breastR.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                //var breastCenter = chest.FindChild(x => (x.Name?.Contains("Boob_Root", StringComparison.InvariantCultureIgnoreCase) ?? false));
                //if (breastCenter?.SceneNode is not null)
                //{
                //    var phys = breastCenter.SceneNode.AddComponent<PhysicsChainComponent>()!;
                //    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Normal;
                //    phys.UpdateRate = 60;
                //    phys.Damping = 0.1f;
                //    phys.Inert = 0.0f;
                //    phys.Stiffness = 0.05f;
                //    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                //    phys.Elasticity = 0.2f;
                //    phys.Multithread = false;
                //}

                var tail = humanComp.Hips?.Node?.Transform.FindDescendant(x =>
                    x.Name?.Contains("Hair_1_2", StringComparison.InvariantCultureIgnoreCase) ?? false);
                if (tail?.SceneNode is not null)
                {
                    var phys = tail.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.5f;
                    phys.Stiffness = 0.05f;
                    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Gravity = new Vector3(0.0f, -0.1f, 0.0f);
                    phys.Elasticity = 0.01f;
                    phys.Multithread = false;
                }

                //var longHair = humanComp.Head?.Node?.Transform.FindDescendant(x =>
                //    x.Name?.Contains("Long Hair", StringComparison.InvariantCultureIgnoreCase) ?? false);
                //if (longHair?.SceneNode is not null)
                //{
                //    //longHair.SceneNode.IsActiveSelf = false;
                //    var phys = longHair.SceneNode.AddComponent<PhysicsChainComponent>()!;
                //    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Normal;
                //    phys.UpdateRate = 60;
                //    phys.Damping = 0.1f;
                //    phys.Inert = 0.0f;
                //    phys.Stiffness = 0.05f;
                //    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                //    phys.Elasticity = 0.2f;
                //}

                //var zafHair = humanComp.Head?.Node?.Transform.FindChild(x => x.Name?.Contains("Zaf Hair", StringComparison.InvariantCultureIgnoreCase) ?? false);
                //if (zafHair?.SceneNode is not null)
                //{
                //    var phys = zafHair.SceneNode.AddComponent<PhysicsChainComponent>()!;
                //    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.FixedUpdate;
                //    phys.UpdateRate = 60;
                //    phys.Damping = 0.01f;
                //    phys.Inert = 0.0f;
                //    phys.Stiffness = 0.01f;
                //    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                //    phys.Elasticity = 0.1f;
                //}
            }
        }

        //Hardcoded materials for testing until UI
        public static void CreateHardcodedMaterial(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            //Debug.Out($"Making material for {name}: {string.Join(", ", textureList.Select(x => x?.Name ?? "<missing name>"))}");

            // Clear current shader list
            mat.Shaders.Clear();

            XRShader color = ShaderHelper.LitColorFragDeferred()!;
            XRShader albedo = ShaderHelper.LitTextureFragDeferred()!;
            XRShader albedoNormal = ShaderHelper.LitTextureNormalFragDeferred()!;
            XRShader albedoNormalMetallic = ShaderHelper.LitTextureNormalMetallicFragDeferred()!;
            XRShader albedoMetallic = ShaderHelper.LitTextureMetallicFragDeferred()!;
            XRShader albedoNormalRoughnessMetallic = ShaderHelper.LitTextureNormalRoughnessMetallicDeferred()!;
            XRShader albedoRoughness = ShaderHelper.LitTextureRoughnessFragDeferred()!;
            XRShader albedoMatcap = ShaderHelper.LitTextureMatcapDeferred()!;
            XRShader albedoEmissive = ShaderHelper.LitTextureEmissiveDeferred();

            switch (name)
            {
                //case "Boots": // "BaseColor, Roughness, BaseColor, , Roughness"
                //              //textureList[0].Load3rdParty();
                //    mat.Shaders.Add(albedoRoughness);
                //    mat.Textures =
                //    [
                //        textureList[0],
                //    textureList[1],
                //];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Metal": // "T_MainTex_D, T_MainTex_D"
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    mat.SetVector3("BaseColor", new Vector3(0.6f));
                //    mat.SetFloat("Roughness", 0.5f);
                //    mat.SetFloat("Metallic", 1.0f);
                //    mat.SetFloat("Specular", 1.0f);
                //    break;

                //case "BackHair": // "HairEarTail Texture, HairEarTail Texture"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures =
                //    [
                //        textureList[0],
                //];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Goth_Bunny_Straps": // "Arm matcaps, Arm matcaps"
                //    mat.Shaders.Add(albedoMatcap);
                //    mat.Textures =
                //    [
                //        textureList.TryGet(0),
                //];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "black_1": // "T_MainTex_D, T_MainTex_D, "
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Ears": // "ears emiss, ears emiss"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures =
                //    [
                //        textureList[0],
                //];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Material #130": // no textures provided
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Material #132": // "BLACK, NORMAL, METALIC, Regular_Roughness"
                //    mat.Shaders.Add(albedoNormalRoughnessMetallic);
                //    mat.Textures =
                //    [
                //        textureList[0],
                //    textureList[1],
                //    textureList[2],
                //    textureList[3],
                //];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "1": // "No Saturation 06 (1), No Saturation 06 (1)
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "2": // "61, 61"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Goth_Bunny_Thigh_Highs": // "generator5, generator5"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "LEFT_EYE": // "Eye_5, left eye by nanna, Eye_5"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Back_hair": // "Hair1, hairglow-mono_emission, Hair1"
                //    mat.Shaders.Add(albedoEmissive);
                //    mat.Textures = [textureList[0], textureList[1]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "New_002": // "lis_dlcu_029_02_base_PP1_K, lis_dlcu_029_02_base_PP1_K"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Mat_Glow": // "T_MainTex_D, T_MainTex_D"
                //    mat.Shaders.Add(color);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "FABRIC 1_FRONT_1830": // No textures given
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Material #131": // "7"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "tail": // "T_MainTex_D, EM backhair, T_MainTex_D"
                //    mat.Shaders.Add(albedoEmissive);
                //    mat.Textures = [textureList[0], textureList[1]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "hoodie": // "low poly_defaultMat.001_BaseColor.1001, low poly_defaultMat.001_Roughness.1001, "
                //    mat.Shaders.Add(albedoRoughness);
                //    mat.Textures = [textureList[0], textureList[1]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Face": // "googleface, googleface"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Material #98": // "Shorts_bake_Merge, Shorts_Normal_OpenGL"
                //    mat.Shaders.Add(albedoNormal);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Material.002": // Empty setup
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                //case "metal.002": // Empty setup
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Body": // "Body, Body"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "RIGHT_EYE": // "Eye_5, right eye by nanna, Eye_5"
                //    mat.Shaders.Add(color);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "TEETH___SOCK": // "T_MainTex_D, T_MainTex_D"
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                default:
                    // Default material setup - use textured deferred if we have valid albedo textures
                    if (textureList.Length > 0 && textureList[0] is not null)
                    {
                        mat.Shaders.Add(albedo);
                        mat.Textures = [textureList[0]];
                    }
                    else
                    {
                        mat.Shaders.Add(color);
                    }
                    MakeDefaultParameters(mat);
                    break;
            }
            mat.Name = name;
            // Set a default render pass (opaque deferred lighting in this example)
            mat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        }
        #endregion

        private static void MakeDefaultParameters(XRMaterial mat)
            => mat.Parameters =
            [
                new ShaderVector3(new Vector3(1.0f, 1.0f, 1.0f), "BaseColor"),
                new ShaderFloat(1.0f, "Opacity"),
                new ShaderFloat(1.0f, "Roughness"),
                new ShaderFloat(0.0f, "Metallic"),
                new ShaderFloat(0.0f, "Specular"),
                new ShaderFloat(0.0f, "Emission"),
            ];
    }
}
