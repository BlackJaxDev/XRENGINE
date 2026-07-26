using MagicPhysX;
using MemoryPack;
using System;
using System.ComponentModel;
using System.Threading;
using System.Linq;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using EngineSettings = XREngine.RuntimeEngine.Rendering.EngineSettings;
using TextureRuntimeLogMode = XREngine.Rendering.TextureRuntimeLogMode;

namespace XREngine
{
    public static partial class EngineRenderingSettingsApplication
    {
            static EngineRenderingSettingsApplication()
                => RuntimeEngine.Rendering.SettingChanged += ApplyRuntimeRenderSettingChange;

            /// <summary>
            /// Forces initialization of the application-owned settings side-effect boundary.
            /// Runtime.Rendering owns settings data and notification; XRENGINE applies world,
            /// window, shader, and renderer consequences.
            /// </summary>
            internal static void InitializeSettingsApplicationBoundary()
            {
            }

            private static void ApplyRuntimeRenderSettingChange(string? propertyName)
            {
                ApplyEngineSettingChange(propertyName);

                RuntimeEngine.Rendering.EngineSettings settings = RuntimeEngine.Rendering.Settings;
                if (propertyName == nameof(RuntimeEngine.Rendering.EngineSettings.AllowSkinning))
                {
                    XREngine.Debug.Rendering(
                        $"[RenderSettings] AllowSkinning changed to {settings.AllowSkinning}; ShaderConfigVersion={settings.ShaderConfigVersion}");
                }

                if (propertyName == nameof(RuntimeEngine.Rendering.EngineSettings.AllowShaderPipelines))
                {
                    NotifyShaderPipelineModeChanged(settings.AllowShaderPipelines);
                    global::XREngine.Rendering.XRMaterial.DisposeShaderPipelineProgramsWhenDisabled();
                    XREngine.Debug.Rendering(
                        $"[RenderSettings] AllowShaderPipelines changed to {settings.AllowShaderPipelines}; ShaderConfigVersion={settings.ShaderConfigVersion}");
                }
            }

            private static void NotifyShaderPipelineModeChanged(bool allowShaderPipelines)
            {
                if (AbstractRenderer.Current is not IRuntimeRendererHost renderer ||
                    !renderer.TryGetBackendCapability<IShaderPipelineModeBackendCapability>(out var capability))
                    return;

                capability?.HandleShaderPipelineModeChanged(allowShaderPipelines);
            }
            private static void ApplyEngineSettingChange(string? propertyName)
            {
                bool applyAll = string.IsNullOrEmpty(propertyName);

                if (applyAll || propertyName == nameof(EngineSettings.CpuSceneCullingStructure))
                    ApplyCpuSceneCullingStructurePreference();

                if (applyAll || propertyName == nameof(EngineSettings.VulkanGpuDrivenProfile)
                    || propertyName == nameof(EngineSettings.EnableZeroReadbackMaterialScatter)
                    || propertyName == nameof(EngineSettings.ZeroReadbackMaterialDrawPath)
                    || propertyName == nameof(EngineSettings.EnableGpuIndirectDebugLogging)
                    || propertyName == nameof(EngineSettings.EnableGpuIndirectValidationLogging)
                    || propertyName == nameof(EngineSettings.EnableGpuIndirectCpuFallback)
                    || propertyName == nameof(EngineSettings.ForceMeshSubmissionStrategy))
                {
                    ApplyGpuRenderDispatchPreference();
                    LogVulkanFeatureProfileFingerprint();
                }

                if (applyAll || propertyName == nameof(EngineSettings.VulkanQueueOverlapMode))
                    LogVulkanFeatureProfileFingerprint();

                if (applyAll || propertyName == nameof(EngineSettings.GpuSortDomainPolicy))
                    LogVulkanFeatureProfileFingerprint();

                if (applyAll
                    || propertyName == nameof(EngineSettings.ClipSpaceYDirection)
                    || propertyName == nameof(EngineSettings.ClipDepthRange))
                {
                    foreach (var window in RuntimeEngine.Windows)
                        window.RequestRenderStateRecheck(resetCircuitBreaker: true);
                }

                if (applyAll || propertyName == nameof(EngineSettings.EnableNvidiaDlss)
                    || propertyName == nameof(EngineSettings.DlssQuality)
                    || propertyName == nameof(EngineSettings.DlssCustomScale)
                    || propertyName == nameof(EngineSettings.DlssSharpness)
                    || propertyName == nameof(EngineSettings.EnableNvidiaDlssFrameGeneration)
                    || propertyName == nameof(EngineSettings.NvidiaDlssFrameGenerationMode))
                {
                    ApplyNvidiaDlssPreference();
                }

                if (applyAll || propertyName == nameof(EngineSettings.EnableIntelXess)
                    || propertyName == nameof(EngineSettings.XessQuality)
                    || propertyName == nameof(EngineSettings.XessCustomScale))
                {
                    ApplyIntelXessPreference();
                }

                if (applyAll || IsRvcPipelineSetting(propertyName))
                    ApplyRenderPipelinePreference();
            }

            private static bool IsRvcPipelineSetting(string? propertyName)
                => propertyName is nameof(EngineSettings.RvcPipelineMode)
                    or nameof(EngineSettings.RvcQuadViewEnabled)
                    or nameof(EngineSettings.RvcStereoReuseEnabled)
                    or nameof(EngineSettings.RvcInsetWideReuseEnabled)
                    or nameof(EngineSettings.RvcTemporalReuseEnabled)
                    or nameof(EngineSettings.RvcPeripheralLightAggregationEnabled)
                    or nameof(EngineSettings.RvcDiagnosticOverlayEnabled)
                    or nameof(EngineSettings.RvcDebugViewMode)
                    or nameof(EngineSettings.RvcLightGridSpace)
                    or nameof(EngineSettings.RvcFovealRadiusDegrees)
                    or nameof(EngineSettings.RvcGuardBandDegrees)
                    or nameof(EngineSettings.RvcMidFieldRadiusDegrees)
                    or nameof(EngineSettings.RvcPeripheralMaxRate)
                    or nameof(EngineSettings.RvcForceFullResNearDistanceMeters)
                    or nameof(EngineSettings.RvcDerivativeStrategy)
                    or nameof(EngineSettings.RvcFovealAntiAliasingPath)
                    or nameof(EngineSettings.RvcReuseMaxNormalAngleDegrees)
                    or nameof(EngineSettings.RvcReuseMaxDepthDeltaMeters)
                    or nameof(EngineSettings.RvcReuseMaxRoughnessBucketDelta);

            public static void ApplyEditorPreferencesChange(string? propertyName)
            {
                bool applyAll = string.IsNullOrEmpty(propertyName);

                if (applyAll || propertyName == nameof(EditorDebugOptions.RenderMesh3DBounds))
                    ApplyRenderMeshBoundsSetting();

                if (applyAll || propertyName == nameof(EditorDebugOptions.VisualizeTransparencyModeOverlay))
                    ApplyRenderMeshBoundsSetting();

                if (applyAll || propertyName == nameof(EditorDebugOptions.VisualizeTransparencyClassificationOverlay))
                    ApplyRenderMeshBoundsSetting();

                if (applyAll ||
                    propertyName == nameof(EditorDebugOptions.VisualizeTransparencyAccumulation) ||
                    propertyName == nameof(EditorDebugOptions.VisualizeTransparencyRevealage) ||
                    propertyName == nameof(EditorDebugOptions.VisualizeTransparencyOverdrawHeatmap))
                    ApplyRenderPipelinePreference();

                if (applyAll || propertyName == nameof(EditorDebugOptions.RenderTransformDebugInfo))
                    ApplyTransformDebugSetting();

                if (applyAll || propertyName == nameof(EditorDebugOptions.UseDebugOpaquePipeline))
                    ApplyRenderPipelinePreference();

                if (applyAll ||
                    propertyName == nameof(EditorDebugOptions.EnableZeroReadbackMaterialScatter) ||
                    propertyName == nameof(EditorDebugOptions.ZeroReadbackMaterialDrawPath))
                {
                    ApplyGpuRenderDispatchPreference();
                    LogVulkanFeatureProfileFingerprint();
                }

                if (applyAll || propertyName == nameof(EditorPreferences.ViewportPresentationMode))
                {
                    foreach (var window in RuntimeEngine.Windows)
                    {
                        window.InvalidateScenePanelResources();
                        window.RequestRenderStateRecheck(resetCircuitBreaker: true);
                    }
                }

                if (applyAll || propertyName == nameof(EditorPreferences.SceneDepthMode))
                    ApplySceneCameraDepthModePreference();

                if (applyAll || propertyName == nameof(EditorPreferences.InteractiveResizeStrategy))
                    Engine.ApplyInteractiveResizeStrategySettings();
            }

            public static global::XREngine.Rendering.XRCamera.EDepthMode ResolveSceneCameraDepthModePreference()
            {
                global::XREngine.Rendering.XRCamera.EDepthMode projectMode = Engine.GameSettings?.DepthModeOverride is { HasOverride: true } projectOverride
                    ? projectOverride.Value
                    : global::XREngine.Rendering.XRCamera.EDepthMode.Normal;

                return Engine.EditorPreferences.SceneDepthMode switch
                {
                    EditorPreferences.ESceneDepthModePreference.Normal => global::XREngine.Rendering.XRCamera.EDepthMode.Normal,
                    EditorPreferences.ESceneDepthModePreference.Reversed => global::XREngine.Rendering.XRCamera.EDepthMode.Reversed,
                    _ => projectMode,
                };
            }

            public static void ApplySceneCameraDepthModePreference()
            {
                global::XREngine.Rendering.XRCamera.EDepthMode depthMode = ResolveSceneCameraDepthModePreference();

                foreach (var worldInstance in Engine.WorldInstances)
                {
                    foreach (SceneNode root in worldInstance.RootNodes)
                    {
                        foreach (SceneNode node in Scene.Prefabs.SceneNodePrefabUtility.EnumerateHierarchy(root))
                        {
                            foreach (var component in node.Components)
                            {
                                if (component is CameraComponent cameraComponent)
                                    cameraComponent.Camera.DepthMode = depthMode;
                            }
                        }
                    }
                }

                foreach (var window in RuntimeEngine.Windows)
                    window.RequestRenderStateRecheck(resetCircuitBreaker: true);
            }

            private static void ApplyRenderMeshBoundsSetting()
            {
                bool renderBounds =
                    Engine.EditorPreferences.Debug.RenderMesh3DBounds ||
                    Engine.EditorPreferences.Debug.VisualizeTransparencyModeOverlay ||
                    Engine.EditorPreferences.Debug.VisualizeTransparencyClassificationOverlay;

                void Apply()
                {
                    foreach (var worldInstance in Engine.WorldInstances)
                    {
                        foreach (SceneNode rootNode in worldInstance.RootNodes)
                        {
                            rootNode.IterateComponents<RenderableComponent>(component =>
                            {
                                foreach (var mesh in component.Meshes.ToArray())
                                    mesh.RenderBounds = renderBounds;
                            }, true);
                        }
                    }
                }

                Engine.EnqueueSwapTask(Apply);
            }

            private static void ApplyTransformDebugSetting()
            {
                bool enable = Engine.EditorPreferences.Debug.RenderTransformDebugInfo;

                void Apply()
                {
                    foreach (var worldInstance in Engine.WorldInstances)
                    {
                        foreach (SceneNode rootNode in worldInstance.RootNodes)
                        {
                            rootNode.IterateHierarchy(node => node.Transform.DebugRender = enable);
                        }
                    }
                }

                Engine.EnqueueSwapTask(Apply);
        }
}
}
