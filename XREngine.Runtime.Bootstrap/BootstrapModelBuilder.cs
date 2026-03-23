using System;
using System.Collections.Generic;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapModelBuilder
{
    public static void ImportModels(string desktopDir, SceneNode rootNode, SceneNode characterParentNode)
    {
        if (!RuntimeBootstrapState.Settings.HasAnyModelsToImport)
            return;

        var importBridge = BootstrapModelImportBridge.Current;
        if (importBridge is null)
        {
            Debug.LogWarning("[BootstrapModelBuilder] Model import requested, but no bootstrap model-import bridge is registered.");
            return;
        }

        if (BootstrapStartupWork.TryQueueDeferredWork(
            () => importBridge.ImportModels(desktopDir, rootNode, characterParentNode),
            "[BootstrapModelBuilder] Deferred startup model imports until the first visible frame."))
            return;

        importBridge.ImportModels(desktopDir, rootNode, characterParentNode);
    }

    public static void AddSkybox(SceneNode rootNode, XRTexture2D? skyEquirect)
    {
        var skybox = new SceneNode(rootNode) { Name = "TestSkyboxNode" };
        if (!skybox.TryAddComponent<SkyboxComponent>(out var skyboxComp))
            return;

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
    }
}