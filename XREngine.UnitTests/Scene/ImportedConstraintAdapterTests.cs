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
public sealed class ImportedConstraintAdapterTests
{
    private const float Tolerance = 1e-4f;

    [Test]
    public void ConstraintAdapters_MapAvatarAndNativeConstraintsAndAssignDependencyOrder()
    {
        using var sandbox = new SourceProjectTestSandbox();
        string prefabPath = sandbox.WriteAsset("Assets/Constraints.prefab", ConstraintPrefabYaml);

        SerializedPrefabConversionResult conversion = SerializedSceneImporter.ImportPrefabWithManifest(prefabPath);

        SceneNode root = conversion.RootNode.ShouldNotBeNull();
        SceneNode a = Child(root, "A");
        SceneNode b = Child(root, "B");
        SceneNode c = Child(root, "C");
        SceneNode d = Child(root, "D");
        SceneNode e = Child(root, "E");
        SceneNode f = Child(root, "F");
        SceneNode g = Child(root, "G");
        SceneNode up = Child(root, "Up");
        WeightedTransformConstraintComponent[] all =
        [
            .. root.Components.OfType<WeightedTransformConstraintComponent>(),
            .. f.Components.OfType<WeightedTransformConstraintComponent>(),
            .. g.Components.OfType<WeightedTransformConstraintComponent>(),
        ];
        all.Length.ShouldBe(9);

        WeightedTransformConstraintComponent scale = Single(all, b, TransformConstraintMode.Match);
        scale.Channels.ShouldBe(TransformConstraintChannels.Scale);
        scale.ScaleOffset.ShouldBeNear(new Vector3(2.0f, 2.0f, 2.0f), Tolerance);
        scale.ScaleAtRest.ShouldBeNear(Vector3.One, Tolerance);
        scale.HasRestPose.ShouldBeTrue();
        scale.Weight.ShouldBe(0.6f, Tolerance);
        scale.ExecutionOrder.ShouldBe(0);

        WeightedTransformConstraintComponent rotation = Single(all, a, TransformConstraintMode.Match);
        rotation.Channels.ShouldBe(TransformConstraintChannels.Rotation);
        rotation.SolveInLocalSpace.ShouldBeTrue();
        rotation.Sources.Count.ShouldBe(2);
        rotation.Sources[0].SourceTransform.ShouldBeSameAs(b.Transform);
        rotation.Sources[1].SourceTransform.ShouldBeSameAs(d.Transform);
        rotation.Sources[1].Weight.ShouldBe(0.0f);
        rotation.ExecutionOrder.ShouldBe(1);

        WeightedTransformConstraintComponent avatarParent = Single(all, c, TransformConstraintMode.Parent);
        avatarParent.Channels.ShouldBe(TransformConstraintChannels.Parent);
        avatarParent.Sources.Single().PositionOffset.ShouldBeNear(new Vector3(0.0f, 0.0f, -1.0f), Tolerance);
        avatarParent.FreezeToWorld.ShouldBeTrue();
        avatarParent.RebakeOffsetsWhenUnfrozen.ShouldBeTrue();
        avatarParent.ExecutionOrder.ShouldBe(2);

        WeightedTransformConstraintComponent[] onE = [.. all.Where(constraint => constraint.TargetTransform == e.Transform)];
        WeightedTransformConstraintComponent avatarLookAt = onE.Single(static constraint => constraint.Sources.Count == 1 && constraint.Roll != 0.0f);
        avatarLookAt.Mode.ShouldBe(TransformConstraintMode.Aim);
        avatarLookAt.AimAxis.ShouldBe(new Vector3(0.0f, 0.0f, -1.0f));
        avatarLookAt.WorldUpType.ShouldBe(TransformConstraintWorldUpType.SceneUp);
        avatarLookAt.Roll.ShouldBe(-30.0f, Tolerance);
        avatarLookAt.ExecutionOrder.ShouldBe(3);

        WeightedTransformConstraintComponent avatarAim = onE.Single(constraint => constraint != avatarLookAt);
        avatarAim.Mode.ShouldBe(TransformConstraintMode.Aim);
        avatarAim.AimAxis.ShouldBe(Vector3.UnitX);
        avatarAim.WorldUpType.ShouldBe(TransformConstraintWorldUpType.ObjectRotationUp);
        avatarAim.WorldUpVector.ShouldBe(new Vector3(0.0f, 0.0f, -1.0f));
        avatarAim.WorldUpTransform.ShouldBeSameAs(up.Transform);
        avatarAim.ExecutionOrder.ShouldBe(0);

        WeightedTransformConstraintComponent nativeParent = Single(all, f, TransformConstraintMode.Parent);
        nativeParent.TargetTransform.ShouldBeSameAs(f.Transform);
        nativeParent.SolveInLocalSpace.ShouldBeFalse();
        nativeParent.Locked.ShouldBeTrue();
        nativeParent.HasRestPose.ShouldBeTrue();
        nativeParent.PositionAtRest.ShouldBeNear(new Vector3(0.0f, 1.0f, 0.0f), Tolerance);
        nativeParent.Sources.Count.ShouldBe(2);
        nativeParent.Sources[0].PositionOffset.ShouldBeNear(new Vector3(1.0f, 2.0f, -3.0f), Tolerance);
        Vector3.Transform(Vector3.UnitX, nativeParent.Sources[0].RotationOffset).ShouldBeNear(new Vector3(0.0f, 0.0f, 1.0f), Tolerance);
        nativeParent.Sources[1].Weight.ShouldBe(0.0f);
        nativeParent.ExecutionOrder.ShouldBe(2);

        WeightedTransformConstraintComponent nativeLookAt = Single(all, f, TransformConstraintMode.Aim);
        nativeLookAt.IsActive.ShouldBeTrue();
        nativeLookAt.WorldUpType.ShouldBe(TransformConstraintWorldUpType.ObjectUp);
        nativeLookAt.WorldUpTransform.ShouldBeSameAs(up.Transform);
        nativeLookAt.Roll.ShouldBe(0.0f);
        nativeLookAt.ExecutionOrder.ShouldBe(3);

        WeightedTransformConstraintComponent nativePosition = Single(all, g, TransformConstraintMode.Match);
        nativePosition.Channels.ShouldBe(TransformConstraintChannels.PositionX | TransformConstraintChannels.PositionZ);
        nativePosition.PositionOffset.ShouldBeNear(new Vector3(0.0f, 0.0f, -2.0f), Tolerance);
        nativePosition.ExecutionOrder.ShouldBe(4);

        WeightedTransformConstraintComponent nativeAim = Single(all, g, TransformConstraintMode.Aim);
        nativeAim.IsActive.ShouldBeFalse();
        nativeAim.AimAxis.ShouldBe(Vector3.UnitY);
        nativeAim.WorldUpType.ShouldBe(TransformConstraintWorldUpType.ObjectUp);
        nativeAim.ExecutionOrder.ShouldBe(4);

        scale.HasDependents.ShouldBeTrue();
        nativePosition.HasDependents.ShouldBeFalse();
        SerializedPrefabImportManifest manifest = conversion.Manifest.ShouldNotBeNull();
        manifest.UnsupportedBehaviours.ShouldBeEmpty();
        manifest.Diagnostics.ShouldNotContain(static diagnostic => diagnostic.Code.StartsWith("SRCCONSTRAINT"));
    }

    private static SceneNode Child(SceneNode root, string name)
        => root.Transform.Children
            .Select(static transform => transform.SceneNode)
            .Single(node => node?.Name == name)!;

    private static WeightedTransformConstraintComponent Single(
        IEnumerable<WeightedTransformConstraintComponent> constraints,
        SceneNode target,
        TransformConstraintMode mode)
        => constraints.Single(constraint => constraint.TargetTransform == target.Transform && constraint.Mode == mode);

    private const string ConstraintPrefabYaml =
        """
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!1 &1
        GameObject:
          m_Component:
          - component: {fileID: 2}
          m_Name: Root
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
          - {fileID: 31}
          - {fileID: 41}
          - {fileID: 51}
          - {fileID: 61}
          - {fileID: 71}
          - {fileID: 81}
          m_Father: {fileID: 0}
        --- !u!1 &10
        GameObject:
          m_Component:
          - component: {fileID: 11}
          m_Name: A
          m_IsActive: 1
        --- !u!4 &11
        Transform:
          m_GameObject: {fileID: 10}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 1, y: 0, z: 0}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 2}
        --- !u!1 &20
        GameObject:
          m_Component:
          - component: {fileID: 21}
          m_Name: B
          m_IsActive: 1
        --- !u!4 &21
        Transform:
          m_GameObject: {fileID: 20}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 0, y: 1, z: 0}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 2}
        --- !u!1 &30
        GameObject:
          m_Component:
          - component: {fileID: 31}
          m_Name: C
          m_IsActive: 1
        --- !u!4 &31
        Transform:
          m_GameObject: {fileID: 30}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 0, y: 0, z: 1}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 2}
        --- !u!1 &40
        GameObject:
          m_Component:
          - component: {fileID: 41}
          m_Name: D
          m_IsActive: 1
        --- !u!4 &41
        Transform:
          m_GameObject: {fileID: 40}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 2, y: 0, z: 0}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 2}
        --- !u!1 &50
        GameObject:
          m_Component:
          - component: {fileID: 51}
          m_Name: Up
          m_IsActive: 1
        --- !u!4 &51
        Transform:
          m_GameObject: {fileID: 50}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 0, y: 3, z: 0}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 2}
        --- !u!1 &60
        GameObject:
          m_Component:
          - component: {fileID: 61}
          m_Name: E
          m_IsActive: 1
        --- !u!4 &61
        Transform:
          m_GameObject: {fileID: 60}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 0, y: 2, z: 2}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 2}
        --- !u!1 &70
        GameObject:
          m_Component:
          - component: {fileID: 71}
          - component: {fileID: 300}
          - component: {fileID: 301}
          m_Name: F
          m_IsActive: 1
        --- !u!4 &71
        Transform:
          m_GameObject: {fileID: 70}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 0, y: 1, z: 0}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 2}
        --- !u!1 &80
        GameObject:
          m_Component:
          - component: {fileID: 81}
          - component: {fileID: 302}
          - component: {fileID: 303}
          m_Name: G
          m_IsActive: 1
        --- !u!4 &81
        Transform:
          m_GameObject: {fileID: 80}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 3, y: 0, z: 3}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 2}
        --- !u!114 &200
        MonoBehaviour:
          m_GameObject: {fileID: 1}
          m_Enabled: 1
          m_Script: {fileID: 41250163, guid: 58e2f01a24261a14cb82e6d3399e8b16, type: 3}
          IsActive: 1
          GlobalWeight: 0.6
          TargetTransform: {fileID: 21}
          SolveInLocalSpace: 1
          FreezeToWorld: 0
          RebakeOffsetsWhenUnfrozen: 0
          Locked: 1
          Sources:
            source0:
              SourceTransform: {fileID: 41}
              Weight: 1
              ParentPositionOffset: {x: 0, y: 0, z: 0}
              ParentRotationOffset: {x: 0, y: 0, z: 0}
            totalLength: 1
            overflowList: []
          ScaleAtRest: {x: 1, y: 1, z: 1}
          ScaleOffset: {x: 2, y: 2, z: 2}
          AffectsScaleX: 1
          AffectsScaleY: 1
          AffectsScaleZ: 1
        --- !u!114 &201
        MonoBehaviour:
          m_GameObject: {fileID: 1}
          m_Enabled: 1
          m_Script: {fileID: 1788371120, guid: 58e2f01a24261a14cb82e6d3399e8b16, type: 3}
          IsActive: 1
          GlobalWeight: 1
          TargetTransform: {fileID: 11}
          SolveInLocalSpace: 1
          FreezeToWorld: 0
          RebakeOffsetsWhenUnfrozen: 0
          Locked: 1
          Sources:
            source0:
              SourceTransform: {fileID: 21}
              Weight: 1
              ParentPositionOffset: {x: 0, y: 0, z: 0}
              ParentRotationOffset: {x: 0, y: 0, z: 0}
            source1:
              SourceTransform: {fileID: 41}
              Weight: 0
              ParentPositionOffset: {x: 0, y: 0, z: 0}
              ParentRotationOffset: {x: 0, y: 0, z: 0}
            source2:
              SourceTransform: {fileID: 31}
              Weight: 0
              ParentPositionOffset: {x: 0, y: 0, z: 0}
              ParentRotationOffset: {x: 0, y: 0, z: 0}
            totalLength: 2
            overflowList: []
          RotationAtRest: {x: 0, y: 0, z: 0}
          RotationOffset: {x: 0, y: 0, z: 0}
          AffectsRotationX: 1
          AffectsRotationY: 1
          AffectsRotationZ: 1
        --- !u!114 &202
        MonoBehaviour:
          m_GameObject: {fileID: 1}
          m_Enabled: 1
          m_Script: {fileID: 575728033, guid: 58e2f01a24261a14cb82e6d3399e8b16, type: 3}
          IsActive: 1
          GlobalWeight: 1
          TargetTransform: {fileID: 31}
          SolveInLocalSpace: 0
          FreezeToWorld: 1
          RebakeOffsetsWhenUnfrozen: 1
          Locked: 1
          Sources:
            source0:
              SourceTransform: {fileID: 11}
              Weight: 1
              ParentPositionOffset: {x: 0, y: 0, z: 1}
              ParentRotationOffset: {x: 0, y: 0, z: 0}
            totalLength: 1
            overflowList: []
          PositionAtRest: {x: 0, y: 0, z: 1}
          AffectsPositionX: 1
          AffectsPositionY: 1
          AffectsPositionZ: 1
          RotationAtRest: {x: 0, y: 0, z: 0}
          AffectsRotationX: 1
          AffectsRotationY: 1
          AffectsRotationZ: 1
        --- !u!114 &203
        MonoBehaviour:
          m_GameObject: {fileID: 1}
          m_Enabled: 1
          m_Script: {fileID: -372946275, guid: 58e2f01a24261a14cb82e6d3399e8b16, type: 3}
          IsActive: 1
          GlobalWeight: 1
          TargetTransform: {fileID: 61}
          SolveInLocalSpace: 0
          FreezeToWorld: 0
          RebakeOffsetsWhenUnfrozen: 0
          Locked: 1
          Sources:
            source0:
              SourceTransform: {fileID: 31}
              Weight: 1
              ParentPositionOffset: {x: 0, y: 0, z: 0}
              ParentRotationOffset: {x: 0, y: 0, z: 0}
            totalLength: 1
            overflowList: []
          RotationAtRest: {x: 0, y: 0, z: 0}
          RotationOffset: {x: 0, y: 0, z: 0}
          WorldUpTransform: {fileID: 0}
          Roll: 30
          UseUpTransform: 0
        --- !u!114 &204
        MonoBehaviour:
          m_GameObject: {fileID: 1}
          m_Enabled: 1
          m_Script: {fileID: -926596935, guid: 58e2f01a24261a14cb82e6d3399e8b16, type: 3}
          IsActive: 1
          GlobalWeight: 1
          TargetTransform: {fileID: 61}
          SolveInLocalSpace: 0
          FreezeToWorld: 0
          RebakeOffsetsWhenUnfrozen: 0
          Locked: 1
          Sources:
            source0:
              SourceTransform: {fileID: 41}
              Weight: 1
              ParentPositionOffset: {x: 0, y: 0, z: 0}
              ParentRotationOffset: {x: 0, y: 0, z: 0}
            totalLength: 1
            overflowList: []
          RotationAtRest: {x: 0, y: 0, z: 0}
          RotationOffset: {x: 0, y: 0, z: 0}
          WorldUpTransform: {fileID: 51}
          AffectsRotationX: 1
          AffectsRotationY: 1
          AffectsRotationZ: 1
          AimAxis: {x: 1, y: 0, z: 0}
          UpAxis: {x: 0, y: 1, z: 0}
          WorldUp: 2
          WorldUpVector: {x: 0, y: 0, z: 1}
        --- !u!1773428102 &300
        ParentConstraint:
          m_GameObject: {fileID: 70}
          m_Enabled: 1
          serializedVersion: 2
          m_Weight: 1
          m_TranslationAtRest: {x: 0, y: 1, z: 0}
          m_RotationAtRest: {x: 0, y: 0, z: 0}
          m_TranslationOffsets:
          - {x: 1, y: 2, z: 3}
          - {x: 0, y: 0, z: 0}
          m_RotationOffsets:
          - {x: 0, y: 90, z: 0}
          - {x: 0, y: 0, z: 0}
          m_AffectTranslationX: 1
          m_AffectTranslationY: 1
          m_AffectTranslationZ: 1
          m_AffectRotationX: 1
          m_AffectRotationY: 1
          m_AffectRotationZ: 1
          m_Active: 1
          m_IsLocked: 1
          m_Sources:
          - sourceTransform: {fileID: 11}
            weight: 1
          - sourceTransform: {fileID: 21}
            weight: 0
        --- !u!1183024399 &301
        LookAtConstraint:
          m_GameObject: {fileID: 70}
          m_Enabled: 1
          m_Weight: 1
          m_RotationAtRest: {x: 0, y: 0, z: 0}
          m_RotationOffset: {x: 0, y: 0, z: 0}
          m_Roll: 45
          m_WorldUpObject: {fileID: 51}
          m_UseUpObject: 1
          m_IsContraintActive: 1
          m_IsLocked: 1
          m_Sources:
          - sourceTransform: {fileID: 31}
            weight: 1
        --- !u!1818360608 &302
        PositionConstraint:
          m_GameObject: {fileID: 80}
          m_Enabled: 1
          serializedVersion: 2
          m_Weight: 1
          m_TranslationAtRest: {x: 3, y: 0, z: 3}
          m_TranslationOffset: {x: 0, y: 0, z: 2}
          m_AffectTranslationX: 1
          m_AffectTranslationY: 0
          m_AffectTranslationZ: 1
          m_Active: 1
          m_IsLocked: 1
          m_Sources:
          - sourceTransform: {fileID: 71}
            weight: 1
        --- !u!895512359 &303
        AimConstraint:
          m_GameObject: {fileID: 80}
          m_Enabled: 1
          serializedVersion: 2
          m_Weight: 1
          m_RotationAtRest: {x: 0, y: 0, z: 0}
          m_RotationOffset: {x: 0, y: 0, z: 0}
          m_AimVector: {x: 0, y: 1, z: 0}
          m_UpVector: {x: 0, y: 0, z: 1}
          m_WorldUpVector: {x: 0, y: 1, z: 0}
          m_WorldUpObject: {fileID: 51}
          m_UpType: 2
          m_AffectRotationX: 1
          m_AffectRotationY: 1
          m_AffectRotationZ: 1
          m_Active: 0
          m_IsLocked: 1
          m_Sources:
          - sourceTransform: {fileID: 61}
            weight: 1
        """;
}
