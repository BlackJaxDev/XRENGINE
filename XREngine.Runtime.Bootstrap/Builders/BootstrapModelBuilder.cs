using System;
using System.Collections.Generic;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap.Builders;

public static class BootstrapModelBuilder
{
    public static void ImportModels(
        string desktopDir,
        SceneNode rootNode,
        SceneNode characterParentNode,
        Action? onAllImportsComplete = null,
        Action? onStaticImportsComplete = null)
    {
        void DispatchStaticImportsComplete()
        {
            if (onStaticImportsComplete is null)
                return;

            _ = Engine.InvokeOnAppThread(
                onStaticImportsComplete,
                "BootstrapModelBuilder: Static model imports completed",
                executeNowIfAlreadyAppThread: true);
        }

        void DispatchAllImportsComplete()
        {
            if (onAllImportsComplete is null)
                return;

            _ = Engine.InvokeOnAppThread(
                onAllImportsComplete,
                "BootstrapModelBuilder: All model imports completed",
                executeNowIfAlreadyAppThread: true);
        }

        UnitTestingWorldSettings settings = RuntimeBootstrapState.Settings;
        if (!settings.CreateUnitBox && !settings.HasAnyModelsToImport)
        {
            DispatchStaticImportsComplete();
            DispatchAllImportsComplete();
            return;
        }

        var importBridge = BootstrapModelImportBridge.Current;
        if (importBridge is null)
        {
            Debug.LogWarning("[BootstrapModelBuilder] Model import requested, but no bootstrap model-import bridge is registered.");
            DispatchStaticImportsComplete();
            DispatchAllImportsComplete();
            return;
        }

        importBridge.ImportModels(desktopDir, rootNode, characterParentNode, onAllImportsComplete, onStaticImportsComplete);
    }

    public static SkyboxComponent? AddSkybox(SceneNode rootNode, XRTexture2D? skyEquirect)
    {
        var skybox = new SceneNode(rootNode) { Name = "TestSkyboxNode" };
        if (!skybox.TryAddComponent<SkyboxComponent>(out var skyboxComp))
            return null;

        skyboxComp!.Name = "TestSkybox";
        skyboxComp.Intensity = 1.0f;

        if (skyEquirect is null)
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
}
