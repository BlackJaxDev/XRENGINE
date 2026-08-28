using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Prefabs;

namespace XREngine.UnitTests.Scene;

[TestFixture]
[NonParallelizable]
public sealed class ImportedAvatarComponentAdapterTests
{
    [Test]
    public void MonoBehaviourAdapters_MapPhysicsConstraintDescriptorAndPreserveUnsupportedMetadata()
    {
        using var sandbox = new SourceProjectTestSandbox();
        string prefabPath = sandbox.WriteAsset(
            "Assets/AvatarBehaviours.prefab",
            """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!1 &1
            GameObject:
              m_Component:
              - component: {fileID: 2}
              - component: {fileID: 100}
              - component: {fileID: 101}
              - component: {fileID: 102}
              - component: {fileID: 103}
              - component: {fileID: 104}
              - component: {fileID: 105}
              - component: {fileID: 106}
              - component: {fileID: 107}
              m_Name: Avatar Root
              m_IsActive: 1
            --- !u!4 &2
            Transform:
              m_GameObject: {fileID: 1}
              m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
              m_LocalPosition: {x: 0, y: 0, z: 0}
              m_LocalScale: {x: 1, y: 1, z: 1}
              m_Children:
              - {fileID: 11}
              - {fileID: 21}
              m_Father: {fileID: 0}
            --- !u!1 &10
            GameObject:
              m_Component:
              - component: {fileID: 11}
              m_Name: Dynamic Bone
              m_IsActive: 1
            --- !u!4 &11
            Transform:
              m_GameObject: {fileID: 10}
              m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
              m_LocalPosition: {x: 0, y: 1, z: 0}
              m_LocalScale: {x: 1, y: 1, z: 1}
              m_Children: []
              m_Father: {fileID: 2}
            --- !u!1 &20
            GameObject:
              m_Component:
              - component: {fileID: 21}
              m_Name: Constraint Source
              m_IsActive: 1
            --- !u!4 &21
            Transform:
              m_GameObject: {fileID: 20}
              m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
              m_LocalPosition: {x: 2, y: 0, z: 3}
              m_LocalScale: {x: 1, y: 1, z: 1}
              m_Children: []
              m_Father: {fileID: 2}
            --- !u!114 &100
            MonoBehaviour:
              m_GameObject: {fileID: 1}
              m_Enabled: 1
              m_Script: {fileID: -1631200402, guid: 2a2c05204084d904aa4945ccff20d8e5, type: 3}
              rootTransform: {fileID: 11}
              shapeType: 0
              radius: 0.2
              height: 0
              position: {x: 0.1, y: 0.2, z: 0.3}
              rotation: {x: 0, y: 0, z: 0, w: 1}
              insideBounds: 0
            --- !u!114 &101
            MonoBehaviour:
              m_GameObject: {fileID: 1}
              m_Enabled: 1
              m_Script: {fileID: -1631200402, guid: 2a2c05204084d904aa4945ccff20d8e5, type: 3}
              rootTransform: {fileID: 11}
              shapeType: 1
              radius: 0.15
              height: 1.1
              position: {x: 0, y: 0, z: 0}
              rotation: {x: 0, y: 0, z: 0, w: 1}
              insideBounds: 1
            --- !u!114 &102
            MonoBehaviour:
              m_GameObject: {fileID: 1}
              m_Enabled: 1
              m_Script: {fileID: -1631200402, guid: 2a2c05204084d904aa4945ccff20d8e5, type: 3}
              rootTransform: {fileID: 11}
              shapeType: 2
              position: {x: 0, y: -0.5, z: 0}
              rotation: {x: 0, y: 0, z: 0, w: 1}
              insideBounds: 0
            --- !u!114 &103
            MonoBehaviour:
              m_GameObject: {fileID: 1}
              m_Enabled: 1
              m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5, type: 3}
              rootTransform: {fileID: 11}
              endpointPosition: {x: 0, y: 0.25, z: 0.5}
              pull: 0.7
              spring: 0.25
              stiffness: 0.4
              immobile: 0.2
              radius: 0.05
              gravity: 0.3
              integrationType: 1
              allowCollision: 1
              ignoreTransforms:
              - {fileID: 21}
              colliders:
              - {fileID: 100}
              - {fileID: 101}
              - {fileID: 102}
              pullCurve:
                m_Curve:
                - time: 0
                  value: 0.25
                  inSlope: 0
                  outSlope: 0
                - time: 1
                  value: 1
                  inSlope: 0
                  outSlope: 0
            --- !u!114 &104
            MonoBehaviour:
              m_GameObject: {fileID: 1}
              m_Enabled: 1
              m_Script: {fileID: 1116338486, guid: 58e2f01a24261a14cb82e6d3399e8b16, type: 3}
              TargetTransform: {fileID: 11}
              GlobalWeight: 0.8
              SolveInLocalSpace: 1
              Locked: 1
              IsActive: 1
              AffectsPositionX: 1
              AffectsRotationY: 1
              Sources:
                0:
                  SourceTransform: {fileID: 21}
                  Weight: 0.75
                  ParentPositionOffset: {x: 0.1, y: 0.2, z: 0.3}
                  ParentRotationOffset: {x: 10, y: 20, z: 30}
                  ScaleOffset: {x: 0.5, y: 0.25, z: 0.125}
            --- !u!114 &105
            MonoBehaviour:
              m_GameObject: {fileID: 1}
              m_Enabled: 1
              m_Script: {fileID: 542108242, guid: 67cc4cb7839cd3741b63733d5adf0442, type: 3}
              avatarRoot: {fileID: 2}
              ViewPosition: {x: 0, y: 1.65, z: 0.2}
              lipSync: 3
              MouthOpenBlendShapeName: MouthOpen
              enableEyeLook: 1
              customEyeLookSettings:
                leftEye: {fileID: 11}
                rightEye: {fileID: 21}
                eyelidType: 0
                eyesLookingStraight:
                  left: {x: 0, y: 0, z: 0, w: 1}
                  right: {x: 0, y: 0, z: 0, w: 1}
              baseAnimationLayers:
              - type: 0
                isEnabled: 1
                isDefault: 1
                animatorController: {fileID: 0}
                mask: {fileID: 0}
            --- !u!114 &106
            MonoBehaviour:
              m_GameObject: {fileID: 1}
              m_Enabled: 1
              m_Script: {fileID: -1427037861, guid: 4ecd63eff847044b68db9453ce219299, type: 3}
              blueprintId: ignored
            --- !u!114 &107
            MonoBehaviour:
              m_GameObject: {fileID: 1}
              m_Enabled: 1
              m_Script: {fileID: 11500000, guid: 99999999999999999999999999999999, type: 3}
              syntheticUnsupportedValue: 42
            """);

        SerializedPrefabConversionResult conversion =
            SerializedSceneImporter.ImportPrefabWithManifest(prefabPath);

        SceneNode root = conversion.RootNode.ShouldNotBeNull();
        SceneNode? boneCandidate = root.Transform.Children
            .Select(static transform => transform.SceneNode)
            .Single(static node => node?.Name == "Dynamic Bone");
        boneCandidate.ShouldNotBeNull();
        SceneNode bone = boneCandidate!;
        SceneNode? sourceCandidate = root.Transform.Children
            .Select(static transform => transform.SceneNode)
            .Single(static node => node?.Name == "Constraint Source");
        sourceCandidate.ShouldNotBeNull();
        SceneNode source = sourceCandidate!;

        PhysicsChainCollider[] volumeColliders =
            root.Components.OfType<PhysicsChainCollider>().ToArray();
        volumeColliders.Length.ShouldBe(2);
        volumeColliders.Single(static collider => collider._height == 0.0f)
            ._radius.ShouldBe(0.2f, 0.0001f);
        volumeColliders.Single(static collider => collider._height > 0.0f)
            ._height.ShouldBe(1.1f, 0.0001f);
        root.Components.OfType<PhysicsChainPlaneCollider>().Count().ShouldBe(1);

        PhysicsChainComponent chain = root.Components.OfType<PhysicsChainComponent>().Single();
        chain.Root.ShouldBeSameAs(bone.Transform);
        chain.EndOffset.ShouldBe(new Vector3(0.0f, 0.25f, -0.5f));
        chain.Elasticity.ShouldBe(0.7f, 0.0001f);
        chain.Damping.ShouldBe(0.75f, 0.0001f);
        chain.Stiffness.ShouldBe(0.4f, 0.0001f);
        chain.Inert.ShouldBe(0.2f, 0.0001f);
        chain.Radius.ShouldBe(0.05f, 0.0001f);
        chain.Gravity.ShouldBe(new Vector3(0.0f, -0.3f, 0.0f));
        chain.UpdateMode.ShouldBe(PhysicsChainComponent.EUpdateMode.FixedUpdate);
        chain.Exclusions.ShouldBe([source.Transform]);
        chain.Colliders.ShouldNotBeNull().Count.ShouldBe(3);
        chain.ElasticityDistrib.ShouldNotBeNull().Keyframes.Count.ShouldBe(2);
        chain.ElasticityDistrib.Keyframes[0].Second.ShouldBe(0.0f);
        chain.ElasticityDistrib.Keyframes[1].Second.ShouldBe(1.0f);
        chain.ElasticityDistrib.Keyframes[1].OutValue.ShouldBe(1.0f);

        WeightedTransformConstraintComponent constraint =
            root.Components.OfType<WeightedTransformConstraintComponent>().Single();
        constraint.TargetTransform.ShouldBeSameAs(bone.Transform);
        constraint.Weight.ShouldBe(0.8f, 0.0001f);
        constraint.SolveInLocalSpace.ShouldBeTrue();
        constraint.Locked.ShouldBeTrue();
        constraint.Channels.ShouldBe(
            TransformConstraintChannels.PositionX |
            TransformConstraintChannels.RotationY);
        TransformConstraintSource constraintSource = constraint.Sources.Single();
        constraintSource.SourceTransform.ShouldBeSameAs(source.Transform);
        constraintSource.Weight.ShouldBe(0.75f, 0.0001f);
        constraintSource.PositionOffset.ShouldBe(new Vector3(0.1f, 0.2f, -0.3f));

        AvatarPresentationComponent descriptor =
            root.Components.OfType<AvatarPresentationComponent>().Single();
        descriptor.AvatarRoot.ShouldBeSameAs(root.Transform);
        descriptor.ViewPosition.ShouldBe(new Vector3(0.0f, 1.65f, -0.2f));
        descriptor.LipSyncMode.ShouldBe(AvatarLipSyncMode.VisemeBlendShape);
        descriptor.MouthOpenBlendShapeName.ShouldBe("MouthOpen");
        descriptor.EyeLook.Enabled.ShouldBeTrue();
        descriptor.EyeLook.LeftEye.ShouldBeSameAs(bone.Transform);
        descriptor.EyeLook.RightEye.ShouldBeSameAs(source.Transform);
        conversion.Manifest.ShouldNotBeNull();
        conversion.Manifest!.AvatarAnimationGraphs.Single().Layers.Single().IsDefault.ShouldBeTrue();

        SerializedPrefabImportManifest manifest = conversion.Manifest!;
        manifest.UnsupportedBehaviours.Count.ShouldBe(1);
        UnsupportedSourceBehaviourMetadata unsupported = manifest.UnsupportedBehaviours.Single();
        unsupported.SerializedPayloadByteCount.ShouldBeGreaterThan(0);
        unsupported.SerializedPayloadSha256.ShouldNotBeNullOrWhiteSpace();
        unsupported.SerializedFieldNames.ShouldContain("syntheticUnsupportedValue");
        manifest.Diagnostics.ShouldContain(static diagnostic => diagnostic.Code == "UNITYVRC0006");
        manifest.Diagnostics.ShouldContain(static diagnostic => diagnostic.Code == "UNITYVRC0007");
        manifest.Diagnostics.ShouldNotContain(static diagnostic =>
            diagnostic.Severity == SourceImportDiagnosticSeverity.Error);
    }
}
