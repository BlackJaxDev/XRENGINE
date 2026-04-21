using XREngine.Components;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;

namespace XREngine.Editor;

public static class BootstrapEditorHookRegistration
{
    public static void Register()
    {
        BootstrapEditorBridge.Current ??= new EditorBootstrapBridge();
        BootstrapWorldBridge.Current ??= new EditorBootstrapWorldBridge();
        BootstrapModelImportBridge.Current ??= new EditorBootstrapModelImportBridge();
        BootstrapInputBridge.Current ??= new EditorBootstrapInputBridge();
    }

    private sealed class EditorBootstrapBridge : IBootstrapEditorBridge
    {
        public void CreateEditorUi(SceneNode parent, CameraComponent? camera, PawnComponent? pawn)
            => EditorUnitTests.UserInterface.CreateEditorUI(parent, camera, pawn);

        public void EnableTransformToolForNode(SceneNode node)
            => EditorUnitTests.UserInterface.EnableTransformToolForNode(node);
    }

    private sealed class EditorBootstrapWorldBridge : IBootstrapWorldBridge
    {
        public XRWorld? CreateSpecializedWorld(UnitTestWorldKind worldKind, bool setUI, bool isServer)
        {
            EditorUnitTests.SyncTogglesFromRuntime();

            XRWorld? world = worldKind switch
            {
                UnitTestWorldKind.AudioTesting => EditorUnitTests.CreateAudioTestingWorld(setUI, isServer),
                UnitTestWorldKind.MathIntersections => EditorUnitTests.CreateMathIntersectionsWorld(setUI, isServer),
                UnitTestWorldKind.MeshEditing => EditorUnitTests.CreateMeshEditingWorld(setUI, isServer),
                UnitTestWorldKind.UberShader => EditorUnitTests.CreateUberShaderWorld(setUI, isServer),
                UnitTestWorldKind.PhysxTesting => EditorUnitTests.CreatePhysxTestingWorld(setUI, isServer),
                _ => null,
            };

            RuntimeBootstrapState.Settings = EditorUnitTests.Toggles.ToRuntimeSettings();
            return world;
        }
    }

    private sealed class EditorBootstrapModelImportBridge : IBootstrapModelImportBridge
    {
        public void ImportModels(string desktopDir, SceneNode rootNode, SceneNode characterParentNode)
            => EditorUnitTests.Models.ImportModels(desktopDir, rootNode, characterParentNode);
    }

    private sealed class EditorBootstrapInputBridge : IBootstrapInputBridge
    {
        public XRComponent? CreateFlyableCameraPawn(SceneNode cameraNode)
            => cameraNode.AddComponent<EditorFlyingCameraPawnComponent>();
    }
}