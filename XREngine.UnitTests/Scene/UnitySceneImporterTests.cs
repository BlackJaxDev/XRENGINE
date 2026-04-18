using System;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Scene.Prefabs;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using TestContext = NUnit.Framework.TestContext;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class UnitySceneImporterTests
{
    [Test]
    public void AssetManager_Load_UnityScene_ImportsDirectHierarchyAndSceneRoots()
    {
        using var sandbox = new UnityImportSandbox();
        string scenePath = Path.Combine(sandbox.AssetsPath, "direct-scene.unity");

        File.WriteAllText(scenePath, """
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &100
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 101}
  m_Layer: 7
  m_Name: Parent Root
  m_TagString: Untagged
  m_IsActive: 1
--- !u!4 &101
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 100}
  m_LocalRotation: {x: 0, y: 0.70710677, z: 0, w: 0.70710677}
  m_LocalPosition: {x: 1, y: 2, z: 3}
  m_LocalScale: {x: 1, y: 2, z: 3}
  m_Children:
  - {fileID: 201}
  m_Father: {fileID: 0}
--- !u!1 &200
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 201}
  m_Layer: 2
  m_Name: Child Node
  m_TagString: Untagged
  m_IsActive: 0
--- !u!4 &201
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 200}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 4, y: 5, z: 6}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 101}
--- !u!1 &300
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 301}
  m_Layer: 0
  m_Name: Canvas Root
  m_TagString: Untagged
  m_IsActive: 1
--- !u!224 &301
RectTransform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 300}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: -2, y: 0.5, z: -9}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 0}
--- !u!1660057539 &9223372036854775807
SceneRoots:
  m_ObjectHideFlags: 0
  m_Roots:
  - {fileID: 301}
  - {fileID: 101}
""");

        AssetManager manager = CreateAssetManager(sandbox);
        try
        {
          XRScene? scene = manager.Load<XRScene>(scenePath);

          scene.ShouldNotBeNull();
          scene.RootNodes.Count.ShouldBe(2);
          scene.RootNodes[0].Name.ShouldBe("Canvas Root");
          scene.RootNodes[1].Name.ShouldBe("Parent Root");
          scene.RootNodes[1].Layer.ShouldBe(7);

          Transform canvasTransform = scene.RootNodes[0].Transform.ShouldBeOfType<Transform>();
          AssertVectorClose(canvasTransform.Translation, new Vector3(-2.0f, 0.5f, 9.0f));

          SceneNode child = scene.RootNodes[1].FirstChild.ShouldNotBeNull();
          child.Name.ShouldBe("Child Node");
          child.IsActiveSelf.ShouldBeFalse();

          Transform parentTransform = scene.RootNodes[1].Transform.ShouldBeOfType<Transform>();
          AssertVectorClose(parentTransform.Translation, new Vector3(1.0f, 2.0f, -3.0f));
          AssertVectorClose(parentTransform.Scale, new Vector3(1.0f, 2.0f, 3.0f));
          AssertQuaternionClose(parentTransform.Rotation, new Quaternion(0.0f, -0.70710677f, 0.0f, 0.70710677f));

          Transform childTransform = child.Transform.ShouldBeOfType<Transform>();
          AssertVectorClose(childTransform.Translation, new Vector3(4.0f, 5.0f, -6.0f));
        }
        finally
        {
          manager.Dispose();
        }
    }

    [Test]
    public void AssetManager_Load_UnityScene_ExpandsPrefabInstancesAndAppliesOverrides()
    {
        using var sandbox = new UnityImportSandbox();
        const string prefabGuid = "24444551b786e1f46ab675c29e3a38c3";

        string prefabPath = Path.Combine(sandbox.AssetsPath, "Character.prefab");
        File.WriteAllText(prefabPath, """
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &10
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 11}
  m_Layer: 3
  m_Name: Prefab Root
  m_TagString: Untagged
  m_IsActive: 0
--- !u!4 &11
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 10}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 1, z: 2}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children:
  - {fileID: 21}
  m_Father: {fileID: 0}
  m_RootOrder: 0
--- !u!1 &20
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 21}
  m_Layer: 1
  m_Name: Prefab Child
  m_TagString: Untagged
  m_IsActive: 1
--- !u!4 &21
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 20}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 3, y: 4, z: 5}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 11}
  m_RootOrder: 0
""");
        File.WriteAllText(prefabPath + ".meta", $"fileFormatVersion: 2{Environment.NewLine}guid: {prefabGuid}{Environment.NewLine}");

        string scenePath = Path.Combine(sandbox.AssetsPath, "prefab-scene.unity");
        File.WriteAllText(scenePath, """
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &600
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 601}
  m_Layer: 0
  m_Name: Loose Root
  m_TagString: Untagged
  m_IsActive: 1
--- !u!4 &601
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 600}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 0}
--- !u!1001 &500
PrefabInstance:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_Modification:
    serializedVersion: 3
    m_TransformParent: {fileID: 0}
    m_Modifications:
    - target: {fileID: 10, guid: __PREFAB_GUID__, type: 3}
      propertyPath: m_Name
      value: Instanced Root
      objectReference: {fileID: 0}
    - target: {fileID: 10, guid: __PREFAB_GUID__, type: 3}
      propertyPath: m_IsActive
      value: 1
      objectReference: {fileID: 0}
    - target: {fileID: 11, guid: __PREFAB_GUID__, type: 3}
      propertyPath: m_RootOrder
      value: 1
      objectReference: {fileID: 0}
    - target: {fileID: 11, guid: __PREFAB_GUID__, type: 3}
      propertyPath: m_LocalPosition.x
      value: -2
      objectReference: {fileID: 0}
    - target: {fileID: 11, guid: __PREFAB_GUID__, type: 3}
      propertyPath: m_LocalPosition.z
      value: 9
      objectReference: {fileID: 0}
    m_RemovedComponents: []
    m_RemovedGameObjects: []
    m_AddedGameObjects: []
    m_AddedComponents: []
  m_SourcePrefab: {fileID: 100100000, guid: __PREFAB_GUID__, type: 3}
--- !u!1660057539 &9223372036854775807
SceneRoots:
  m_ObjectHideFlags: 0
  m_Roots:
  - {fileID: 601}
  - {fileID: 500}
""".Replace("__PREFAB_GUID__", prefabGuid, StringComparison.Ordinal));

        AssetManager manager = CreateAssetManager(sandbox);
        try
        {
          XRScene? scene = manager.Load<XRScene>(scenePath);

          scene.ShouldNotBeNull();
          scene.RootNodes.Count.ShouldBe(2);
          scene.RootNodes[0].Name.ShouldBe("Loose Root");
          scene.RootNodes[1].Name.ShouldBe("Instanced Root");
          scene.RootNodes[1].IsActiveSelf.ShouldBeTrue();

          Transform prefabRootTransform = scene.RootNodes[1].Transform.ShouldBeOfType<Transform>();
          AssertVectorClose(prefabRootTransform.Translation, new Vector3(-2.0f, 1.0f, -9.0f));

          SceneNode prefabChild = scene.RootNodes[1].FirstChild.ShouldNotBeNull();
          prefabChild.Name.ShouldBe("Prefab Child");
          prefabChild.Layer.ShouldBe(1);

          Transform prefabChildTransform = prefabChild.Transform.ShouldBeOfType<Transform>();
          AssertVectorClose(prefabChildTransform.Translation, new Vector3(3.0f, 4.0f, -5.0f));
        }
        finally
        {
          manager.Dispose();
        }
    }

    [Test]
    public void AssetManager_Load_UnityScene_ImportsSupportedComponents()
    {
        using var sandbox = new UnityImportSandbox();
        const string materialGuid = "fc14e306a65185a40b0b553303bf7889";

        string materialPath = Path.Combine(sandbox.AssetsPath, "MeshMaterial.mat");
        File.WriteAllText(materialPath, """
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  m_ObjectHideFlags: 0
  m_Name: Mesh Material
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs:
    - _MainTex:
        m_Texture: {fileID: 0}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    m_Colors:
    - _Color: {r: 0.25, g: 0.5, b: 0.75, a: 1}
""");
        File.WriteAllText(materialPath + ".meta", $"fileFormatVersion: 2{Environment.NewLine}guid: {materialGuid}{Environment.NewLine}");

        string scenePath = Path.Combine(sandbox.AssetsPath, "components-scene.unity");
        File.WriteAllText(scenePath, """
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &100
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 101}
  - component: {fileID: 102}
  m_Layer: 0
  m_Name: Main Camera
  m_TagString: MainCamera
  m_IsActive: 1
--- !u!4 &101
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 100}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 1, z: -10}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 0}
--- !u!20 &102
Camera:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 100}
  m_Enabled: 1
  near clip plane: 0.5
  far clip plane: 200
  field of view: 70
  orthographic: 0
  orthographic size: 5
--- !u!1 &200
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 201}
  - component: {fileID: 202}
  m_Layer: 0
  m_Name: Directional Light
  m_TagString: Untagged
  m_IsActive: 1
--- !u!4 &201
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 200}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 3, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 0}
--- !u!108 &202
Light:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 200}
  m_Enabled: 1
  m_Type: 1
  m_Color: {r: 0.8, g: 0.7, b: 0.6, a: 1}
  m_Intensity: 2.5
  m_Range: 10
  m_SpotAngle: 30
  m_InnerSpotAngle: 20
  m_Shadows:
    m_Type: 0
--- !u!1 &300
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 301}
  - component: {fileID: 302}
  - component: {fileID: 303}
  m_Layer: 0
  m_Name: Rendered Cube
  m_TagString: Untagged
  m_IsActive: 1
--- !u!4 &301
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 300}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 2, y: 0, z: 1}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 0}
--- !u!33 &302
MeshFilter:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 300}
  m_Mesh: {fileID: 10202, guid: 0000000000000000e000000000000000, type: 0}
--- !u!23 &303
MeshRenderer:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 300}
  m_Enabled: 1
  m_CastShadows: 0
  m_ReceiveShadows: 0
  m_Materials:
  - {fileID: 2100000, guid: __MAT_GUID__, type: 2}
--- !u!1660057539 &9223372036854775807
SceneRoots:
  m_ObjectHideFlags: 0
  m_Roots:
  - {fileID: 101}
  - {fileID: 201}
  - {fileID: 301}
""".Replace("__MAT_GUID__", materialGuid, StringComparison.Ordinal));

        AssetManager manager = CreateAssetManager(sandbox);
        try
        {
            XRScene? scene = manager.Load<XRScene>(scenePath);

            scene.ShouldNotBeNull();
            scene.RootNodes.Count.ShouldBe(3);

            scene.RootNodes[0].TryGetComponent<CameraComponent>(out CameraComponent? cameraComponent).ShouldBeTrue();
            cameraComponent.ShouldNotBeNull();
            cameraComponent.Camera.Parameters.ShouldBeOfType<XRPerspectiveCameraParameters>().VerticalFieldOfView.ShouldBe(70.0f, 0.0001f);
            cameraComponent.Camera.NearZ.ShouldBe(0.5f, 0.0001f);
            cameraComponent.Camera.FarZ.ShouldBe(200.0f, 0.0001f);

            scene.RootNodes[1].TryGetComponent<DirectionalLightComponent>(out DirectionalLightComponent? lightComponent).ShouldBeTrue();
            lightComponent.ShouldNotBeNull();
            lightComponent.DiffuseIntensity.ShouldBe(2.5f, 0.0001f);
            lightComponent.Color.R.ShouldBe(0.8f, 0.0001f);
            lightComponent.CastsShadows.ShouldBeFalse();

            scene.RootNodes[2].TryGetComponent<ModelComponent>(out ModelComponent? modelComponent).ShouldBeTrue();
            modelComponent.ShouldNotBeNull();
            modelComponent.Model.ShouldNotBeNull();
            modelComponent.Model.Meshes.Count.ShouldBe(1);
            modelComponent.MeshCastsShadows.ShouldBe(false);
            modelComponent.Meshes.Count.ShouldBe(1);
            modelComponent.Meshes[0].RenderInfo.ReceivesShadows.ShouldBeFalse();
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Test]
    public void AssetManager_Load_UnityPrefab_ImportsPrefabSourceAsset()
    {
        using var sandbox = new UnityImportSandbox();

        string prefabPath = Path.Combine(sandbox.AssetsPath, "Imported.prefab");
        File.WriteAllText(prefabPath, """
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &10
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 11}
  - component: {fileID: 12}
  - component: {fileID: 13}
  m_Layer: 0
  m_Name: Prefab Root
  m_TagString: Untagged
  m_IsActive: 1
--- !u!4 &11
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 10}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 0}
--- !u!33 &12
MeshFilter:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 10}
  m_Mesh: {fileID: 10207, guid: 0000000000000000e000000000000000, type: 0}
--- !u!23 &13
MeshRenderer:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 10}
  m_Enabled: 1
  m_CastShadows: 1
  m_ReceiveShadows: 1
  m_Materials: []
""");

        AssetManager manager = CreateAssetManager(sandbox);
        try
        {
            XRPrefabSource? prefab = manager.Load<XRPrefabSource>(prefabPath);

            prefab.ShouldNotBeNull();
            prefab.RootNode.ShouldNotBeNull();
            prefab.RootNode.Name.ShouldBe("Prefab Root");
            prefab.RootNode.TryGetComponent<ModelComponent>(out ModelComponent? modelComponent).ShouldBeTrue();
            modelComponent.ShouldNotBeNull();
            modelComponent.Model.ShouldNotBeNull();
            modelComponent.Model.Meshes.Count.ShouldBe(1);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Test]
    public void AssetManager_Load_UnityScene_AppliesPrefabAddRemoveDeltas()
    {
        using var sandbox = new UnityImportSandbox();
        const string prefabGuid = "2fbc33abead90dc4da7d57007fdeb625";

        string prefabPath = Path.Combine(sandbox.AssetsPath, "DeltaSource.prefab");
        File.WriteAllText(prefabPath, """
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &10
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 11}
  - component: {fileID: 12}
  m_Layer: 0
  m_Name: Prefab Root
  m_TagString: Untagged
  m_IsActive: 1
--- !u!4 &11
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 10}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children:
  - {fileID: 21}
  m_Father: {fileID: 0}
--- !u!108 &12
Light:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 10}
  m_Enabled: 1
  m_Type: 1
  m_Color: {r: 1, g: 1, b: 1, a: 1}
  m_Intensity: 1
  m_Range: 10
  m_SpotAngle: 30
  m_InnerSpotAngle: 20
  m_Shadows:
    m_Type: 0
--- !u!1 &20
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 21}
  m_Layer: 0
  m_Name: Removed Child
  m_TagString: Untagged
  m_IsActive: 1
--- !u!4 &21
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 20}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 1, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 11}
""");
        File.WriteAllText(prefabPath + ".meta", $"fileFormatVersion: 2{Environment.NewLine}guid: {prefabGuid}{Environment.NewLine}");

        string scenePath = Path.Combine(sandbox.AssetsPath, "delta-scene.unity");
        File.WriteAllText(scenePath, """
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &700
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 701}
  m_Layer: 0
  m_Name: Added Child
  m_TagString: Untagged
  m_IsActive: 1
--- !u!4 &701
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 700}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: -2, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 0}
--- !u!20 &703
Camera:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 10}
  m_Enabled: 1
  near clip plane: 0.2
  far clip plane: 50
  field of view: 55
  orthographic: 0
  orthographic size: 5
--- !u!1001 &500
PrefabInstance:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_Modification:
    serializedVersion: 3
    m_TransformParent: {fileID: 0}
    m_Modifications:
    - target: {fileID: 10, guid: __PREFAB_GUID__, type: 3}
      propertyPath: m_Name
      value: Delta Root
      objectReference: {fileID: 0}
    m_RemovedComponents:
    - {fileID: 12, guid: __PREFAB_GUID__, type: 3}
    m_RemovedGameObjects:
    - {fileID: 20, guid: __PREFAB_GUID__, type: 3}
    m_AddedGameObjects:
    - targetCorrespondingSourceObject: {fileID: 11, guid: __PREFAB_GUID__, type: 3}
      insertIndex: 0
      addedObject: {fileID: 700}
    m_AddedComponents:
    - targetCorrespondingSourceObject: {fileID: 10, guid: __PREFAB_GUID__, type: 3}
      insertIndex: 0
      addedObject: {fileID: 703}
  m_SourcePrefab: {fileID: 100100000, guid: __PREFAB_GUID__, type: 3}
--- !u!1660057539 &9223372036854775807
SceneRoots:
  m_ObjectHideFlags: 0
  m_Roots:
  - {fileID: 500}
""".Replace("__PREFAB_GUID__", prefabGuid, StringComparison.Ordinal));

        AssetManager manager = CreateAssetManager(sandbox);
        try
        {
            XRScene? scene = manager.Load<XRScene>(scenePath);

            scene.ShouldNotBeNull();
            scene.RootNodes.Count.ShouldBe(1);
            scene.RootNodes[0].Name.ShouldBe("Delta Root");

            scene.RootNodes[0].TryGetComponent<DirectionalLightComponent>(out DirectionalLightComponent? removedLight).ShouldBeFalse();
            scene.RootNodes[0].TryGetComponent<CameraComponent>(out CameraComponent? addedCamera).ShouldBeTrue();
            addedCamera.ShouldNotBeNull();
            addedCamera.Camera.NearZ.ShouldBe(0.2f, 0.0001f);
            addedCamera.Camera.FarZ.ShouldBe(50.0f, 0.0001f);

            scene.RootNodes[0].Transform.Children.Count.ShouldBe(1);
            SceneNode addedChild = scene.RootNodes[0].FirstChild.ShouldNotBeNull();
            addedChild.Name.ShouldBe("Added Child");
            Transform addedChildTransform = addedChild.Transform.ShouldBeOfType<Transform>();
            AssertVectorClose(addedChildTransform.Translation, new Vector3(-2.0f, 0.0f, 0.0f));
        }
        finally
        {
            manager.Dispose();
        }
    }

    private static AssetManager CreateAssetManager(UnityImportSandbox sandbox)
    {
        var manager = new AssetManager
        {
            MonitorGameAssetsForChanges = false,
            GameAssetsPath = sandbox.AssetsPath,
            GameCachePath = sandbox.CachePath,
        };
        return manager;
    }

    private static void AssertVectorClose(Vector3 actual, Vector3 expected, float tolerance = 0.0001f)
    {
        actual.X.ShouldBe(expected.X, tolerance);
        actual.Y.ShouldBe(expected.Y, tolerance);
        actual.Z.ShouldBe(expected.Z, tolerance);
    }

    private static void AssertQuaternionClose(Quaternion actual, Quaternion expected, float tolerance = 0.0001f)
    {
        actual.X.ShouldBe(expected.X, tolerance);
        actual.Y.ShouldBe(expected.Y, tolerance);
        actual.Z.ShouldBe(expected.Z, tolerance);
        actual.W.ShouldBe(expected.W, tolerance);
    }

    private sealed class UnityImportSandbox : IDisposable
    {
        public UnityImportSandbox()
        {
            RootPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "UnitySceneImport", Guid.NewGuid().ToString("N"));
            AssetsPath = Path.Combine(RootPath, "Assets");
            CachePath = Path.Combine(RootPath, "Cache");
            Directory.CreateDirectory(AssetsPath);
            Directory.CreateDirectory(CachePath);
        }

        public string RootPath { get; }
        public string AssetsPath { get; }
        public string CachePath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                    Directory.Delete(RootPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}