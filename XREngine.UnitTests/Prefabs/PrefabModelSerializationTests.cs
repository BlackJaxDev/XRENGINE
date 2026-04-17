using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Prefabs;

[TestFixture]
public sealed class PrefabModelSerializationTests
{
    [Test]
    public void YamlDeserializer_RoundTrips_ModelWithSingleSubMesh()
    {
        Model original = CreateTriangleModel(modelName: "InlineModel", subMeshName: "InlineSubMesh");

        string yaml = AssetManager.Serializer.Serialize(original);
        yaml.ShouldContain("Bytes:");
        Model clone = AssetManager.Deserializer.Deserialize<Model>(yaml);

        AssertModelShape(clone, "InlineSubMesh");
    }

    [Test]
    public void DataSourceYaml_RoundTrips_RuntimeCookedMeshPayload()
    {
        XRMesh mesh = CreateTriangleMesh("PayloadMesh");
        byte[] payload = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => RuntimeCookedBinarySerializer.Serialize(mesh));

        DataSource source = new(payload)
        {
            PreferCompressedYaml = true
        };

        string yaml = AssetManager.Serializer.Serialize(source);
        yaml.ShouldContain("Bytes:");

        DataSource clone = AssetManager.Deserializer.Deserialize<DataSource>(yaml);

        clone.GetBytes().ShouldBe(payload);
    }

    [Test]
    public void RuntimeCookedBinary_RoundTrips_CreateTrianglesMesh()
    {
        XRMesh mesh = CreateTriangleMesh("RuntimeCookedMesh");
        byte[] payload = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => RuntimeCookedBinarySerializer.Serialize(mesh));

        XRMesh clone = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => RuntimeCookedBinarySerializer.Deserialize(typeof(XRMesh), payload) as XRMesh).ShouldNotBeNull();

        clone.VertexCount.ShouldBe(mesh.VertexCount);
        clone.Buffers.Count.ShouldBe(mesh.Buffers.Count);
        clone.Type.ShouldBe(mesh.Type);
    }

    [Test]
    public void RuntimeCookedBinary_RoundTrips_SkinningBlendshapesAndSerializedBoneIds()
    {
        Transform bone = new()
        {
            Name = "Bone"
        };

        XRMesh mesh = CreateSkinnedBlendshapeMesh("CookedSkinnedBlendshapeMesh", bone, "Smile");
        byte[] payload = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => RuntimeCookedBinarySerializer.Serialize(mesh));

        XRMesh clone = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => RuntimeCookedBinarySerializer.Deserialize(typeof(XRMesh), payload) as XRMesh).ShouldNotBeNull();

        clone.HasSkinning.ShouldBeTrue();
        clone.HasBlendshapes.ShouldBeTrue();
        clone.BlendshapeNames.ShouldContain("Smile");
        clone.UtilizedBones.Length.ShouldBe(1);
        clone.UtilizedBones[0].tfm.SerializedReferenceId.ShouldBe(bone.ID);
        clone.BoneWeightOffsets.ShouldNotBeNull();
        clone.BoneWeightCounts.ShouldNotBeNull();
    }

    [Test]
    public void CloneHierarchy_PreservesInlineModelSubmeshes()
    {
        SceneNode template = CreateSceneNodeWithModel(CreateTriangleModel(modelName: "InlinePrefabModel", subMeshName: "InlinePrefabSubMesh"));

        SceneNode clone = SceneNodePrefabUtility.CloneHierarchy(template);

        AssertNodeModelShape(clone, "InlinePrefabSubMesh");
    }

    [Test]
    public void CloneHierarchy_PreservesExternalModelSubmeshes()
    {
        string assetsRoot = Engine.Assets.GameAssetsPath;
        string metadataRoot = Engine.Assets.GameMetadataPath.ShouldNotBeNull();
        string relativeFolder = Path.Combine("_PrefabModelSerializationTests", Guid.NewGuid().ToString("N"));
        string assetDirectory = Path.Combine(assetsRoot, relativeFolder);
        string metadataDirectory = Path.Combine(metadataRoot, relativeFolder);

        Directory.CreateDirectory(assetDirectory);

        try
        {
            string modelPath = Path.Combine(assetDirectory, "ExternalModel.asset");
            Model externalModel = CreateTriangleModel(modelName: "ExternalPrefabModel", subMeshName: "ExternalPrefabSubMesh");
            externalModel.FilePath = modelPath;
            externalModel.SourceAsset = externalModel;

            XRAssetGraphUtility.RefreshAssetGraph(externalModel);
            externalModel.SerializeTo(modelPath, AssetManager.Serializer);
            WriteMetadataForGameAsset(modelPath, externalModel.ID);

            SceneNode template = CreateSceneNodeWithModel(externalModel);
            string yaml = AssetManager.Serializer.Serialize(template);
            yaml.ShouldContain(externalModel.ID.ToString());

            SceneNode clone = SceneNodePrefabUtility.CloneHierarchy(template);

            AssertNodeModelShape(clone, "ExternalPrefabSubMesh");
        }
        finally
        {
            DeleteDirectoryIfExists(assetDirectory);
            DeleteDirectoryIfExists(metadataDirectory);
        }
    }

    [Test]
    public void SceneNodeYaml_ExternalModel_EmitsIdReferenceInsteadOfInlineBody()
    {
        string assetsRoot = Engine.Assets.GameAssetsPath;
        string metadataRoot = Engine.Assets.GameMetadataPath.ShouldNotBeNull();
        string relativeFolder = Path.Combine("_PrefabModelSerializationTests", Guid.NewGuid().ToString("N"));
        string assetDirectory = Path.Combine(assetsRoot, relativeFolder);
        string metadataDirectory = Path.Combine(metadataRoot, relativeFolder);

        Directory.CreateDirectory(assetDirectory);

        try
        {
            string modelPath = Path.Combine(assetDirectory, "ExternalModel.asset");
            Model externalModel = CreateTriangleModel(modelName: "ExternalSceneModel", subMeshName: "ExternalSceneSubMesh");
            externalModel.FilePath = modelPath;
            externalModel.SourceAsset = externalModel;

            XRAssetGraphUtility.RefreshAssetGraph(externalModel);
            externalModel.SerializeTo(modelPath, AssetManager.Serializer);
            WriteMetadataForGameAsset(modelPath, externalModel.ID);

            SceneNode template = CreateSceneNodeWithModel(externalModel);

            string yaml = AssetManager.Serializer.Serialize(template);

            yaml.ShouldContain($"ID: {externalModel.ID}");
            yaml.ShouldNotContain($"Name: {externalModel.Name}");
            yaml.ShouldNotContain("Meshes:");
        }
        finally
        {
            DeleteDirectoryIfExists(assetDirectory);
            DeleteDirectoryIfExists(metadataDirectory);
        }
    }

    [Test]
    public void PrefabSourceYaml_ExternalModel_EmitsIdReferenceInsteadOfInlineBody()
    {
        string assetsRoot = Engine.Assets.GameAssetsPath;
        string metadataRoot = Engine.Assets.GameMetadataPath.ShouldNotBeNull();
        string relativeFolder = Path.Combine("_PrefabModelSerializationTests", Guid.NewGuid().ToString("N"));
        string assetDirectory = Path.Combine(assetsRoot, relativeFolder);
        string metadataDirectory = Path.Combine(metadataRoot, relativeFolder);

        Directory.CreateDirectory(assetDirectory);

        try
        {
            string modelPath = Path.Combine(assetDirectory, "ExternalModel.asset");
            Model externalModel = CreateTriangleModel(modelName: "ExternalPrefabModel", subMeshName: "ExternalPrefabSubMesh");
            externalModel.FilePath = modelPath;
            externalModel.SourceAsset = externalModel;

            XRAssetGraphUtility.RefreshAssetGraph(externalModel);
            externalModel.SerializeTo(modelPath, AssetManager.Serializer);
            WriteMetadataForGameAsset(modelPath, externalModel.ID);

            XRPrefabSource prefab = new()
            {
                RootNode = CreateSceneNodeWithModel(externalModel)
            };

            string yaml = AssetManager.Serializer.Serialize(prefab);

            yaml.ShouldContain($"ID: {externalModel.ID}");
            yaml.ShouldNotContain($"Name: {externalModel.Name}");
            yaml.ShouldNotContain("Meshes:");
        }
        finally
        {
            DeleteDirectoryIfExists(assetDirectory);
            DeleteDirectoryIfExists(metadataDirectory);
        }
    }

    [Test]
    public void ModelYaml_ExternalSubMesh_EmitsIdReferenceInsteadOfInlineBody()
    {
        string assetsRoot = Engine.Assets.GameAssetsPath;
        string metadataRoot = Engine.Assets.GameMetadataPath.ShouldNotBeNull();
        string relativeFolder = Path.Combine("_PrefabModelSerializationTests", Guid.NewGuid().ToString("N"));
        string assetDirectory = Path.Combine(assetsRoot, relativeFolder);
        string metadataDirectory = Path.Combine(metadataRoot, relativeFolder);

        Directory.CreateDirectory(assetDirectory);

        try
        {
            XRMesh mesh = CreateTriangleMesh("ExternalSubMesh_Mesh");
            XRMaterial material = new()
            {
                Name = "ReferencedMaterial"
            };

            string subMeshPath = Path.Combine(assetDirectory, "ExternalSubMesh.asset");
            SubMesh externalSubMesh = new(new SubMeshLOD(material, mesh, 0.0f))
            {
                Name = "ExternalSubMesh",
                RootBone = new Transform { Name = "ReferencedBone" },
                RootTransform = new Transform { Name = "ReferencedRoot" }
            };
            externalSubMesh.FilePath = subMeshPath;
            externalSubMesh.SourceAsset = externalSubMesh;

            XRAssetGraphUtility.RefreshAssetGraph(externalSubMesh);
            externalSubMesh.SerializeTo(subMeshPath, AssetManager.Serializer);
            WriteMetadataForGameAsset(subMeshPath, externalSubMesh.ID);

            Model model = new(externalSubMesh)
            {
                Name = "ReferencedOwnerModel"
            };

            string yaml = AssetManager.Serializer.Serialize(model);

            yaml.ShouldContain($"ID: {externalSubMesh.ID}");
            yaml.ShouldNotContain($"Name: {externalSubMesh.Name}");
            yaml.ShouldNotContain("RootBone:");
            yaml.ShouldNotContain("RootTransform:");
            yaml.ShouldNotContain("LODs:");
        }
        finally
        {
            DeleteDirectoryIfExists(assetDirectory);
            DeleteDirectoryIfExists(metadataDirectory);
        }
    }

    [Test]
    public void SubMeshYaml_ExternalMaterial_EmitsIdReferenceInsteadOfInlineBody()
    {
        string assetsRoot = Engine.Assets.GameAssetsPath;
        string metadataRoot = Engine.Assets.GameMetadataPath.ShouldNotBeNull();
        string relativeFolder = Path.Combine("_PrefabModelSerializationTests", Guid.NewGuid().ToString("N"));
        string assetDirectory = Path.Combine(assetsRoot, relativeFolder);
        string metadataDirectory = Path.Combine(metadataRoot, relativeFolder);

        Directory.CreateDirectory(assetDirectory);

        try
        {
            string materialPath = Path.Combine(assetDirectory, "ExternalMaterial.asset");
            XRMaterial externalMaterial = new()
            {
                Name = "ExternalSubMeshMaterial"
            };
            externalMaterial.FilePath = materialPath;
            externalMaterial.SourceAsset = externalMaterial;

            XRAssetGraphUtility.RefreshAssetGraph(externalMaterial);
            externalMaterial.SerializeTo(materialPath, AssetManager.Serializer);
            WriteMetadataForGameAsset(materialPath, externalMaterial.ID);

            SubMesh subMesh = new(new SubMeshLOD(externalMaterial, CreateTriangleMesh("ExternalMaterialMesh"), 0.0f))
            {
                Name = "ExternalMaterialSubMesh"
            };

            string yaml = AssetManager.Serializer.Serialize(subMesh);

            yaml.ShouldContain($"ID: {externalMaterial.ID}");
            yaml.ShouldNotContain($"Name: {externalMaterial.Name}");
        }
        finally
        {
            DeleteDirectoryIfExists(assetDirectory);
            DeleteDirectoryIfExists(metadataDirectory);
        }
    }

    [Test]
    public void SubMeshYaml_SceneBoundTransformReferences_EmitIdOnly()
    {
        SceneNode root = new("TransformReferenceRoot");
        SceneNode boneNode = new("TransformReferenceBone");
        boneNode.Parent = root;

        SubMesh subMesh = new(new SubMeshLOD(new XRMaterial { Name = "ReferenceMaterial" }, CreateTriangleMesh("ReferenceMesh"), 0.0f))
        {
            Name = "ReferenceSubMesh",
            RootBone = boneNode.Transform,
            RootTransform = root.Transform,
        };

        string yaml = AssetManager.Serializer.Serialize(subMesh);

        yaml.ShouldContain($"RootBone:{Environment.NewLine}  ID: {boneNode.Transform.EffectiveSerializedReferenceId}");
        yaml.ShouldContain($"RootTransform:{Environment.NewLine}  ID: {root.Transform.EffectiveSerializedReferenceId}");
        yaml.ShouldNotContain("$value");
        yaml.ShouldNotContain("Translation:");
    }

    [Test]
    public void Serializer_ResetsTransformReferenceWriteStateBetweenTopLevelDocuments()
    {
        SceneNode root = new("LeakReferenceRoot");
        SceneNode boneNode = new("LeakReferenceBone");
        boneNode.Parent = root;

        SubMesh subMesh = new(new SubMeshLOD(new XRMaterial { Name = "LeakReferenceMaterial" }, CreateTriangleMesh("LeakReferenceMesh"), 0.0f))
        {
            Name = "LeakReferenceSubMesh",
            RootBone = boneNode.Transform,
            RootTransform = root.Transform,
        };

        string referenceYaml = AssetManager.Serializer.Serialize(subMesh);
        referenceYaml.ShouldContain($"RootBone:{Environment.NewLine}  ID: {boneNode.Transform.EffectiveSerializedReferenceId}");
        referenceYaml.ShouldContain($"RootTransform:{Environment.NewLine}  ID: {root.Transform.EffectiveSerializedReferenceId}");

        SceneNode freshNode = new("FreshTransformNode");
        string yaml = AssetManager.Serializer.Serialize(freshNode);

        yaml.ShouldContain("$value:");
        yaml.ShouldNotContain($"Transform:{Environment.NewLine}  ID: {freshNode.Transform.EffectiveSerializedReferenceId}");
    }

    [Test]
    public void SceneNodeYaml_ExternalSkinnedSubMesh_RebindsBonesAndPreservesBlendshapes()
    {
        string assetsRoot = Engine.Assets.GameAssetsPath;
        string metadataRoot = Engine.Assets.GameMetadataPath.ShouldNotBeNull();
        string relativeFolder = Path.Combine("_PrefabModelSerializationTests", Guid.NewGuid().ToString("N"));
        string assetDirectory = Path.Combine(assetsRoot, relativeFolder);
        string metadataDirectory = Path.Combine(metadataRoot, relativeFolder);
        bool previousMonitorSetting = Engine.Assets.MonitorGameAssetsForChanges;

        Directory.CreateDirectory(assetDirectory);

        try
        {
            Engine.Assets.MonitorGameAssetsForChanges = false;

            SceneNode root = new("PrefabSkinnedRoot");
            SceneNode meshNode = new("MeshNode");
            meshNode.Parent = root;

            SceneNode boneNode = new("BoneA");
            boneNode.Parent = root;

            XRMesh skinnedMesh = CreateSkinnedBlendshapeMesh("ExternalSkinnedMesh", boneNode.Transform, "Smile");
            string meshPath = Path.Combine(assetDirectory, "ExternalSkinnedMesh.asset");
            skinnedMesh.FilePath = meshPath;
            skinnedMesh.SourceAsset = skinnedMesh;
            skinnedMesh.SerializeTo(meshPath, AssetManager.Serializer);
            WriteMetadataForGameAsset(meshPath, skinnedMesh.ID);

            XRMaterial material = new()
            {
                Name = "ExternalSkinnedMaterial"
            };
            string materialPath = Path.Combine(assetDirectory, "ExternalSkinnedMaterial.asset");
            material.FilePath = materialPath;
            material.SourceAsset = material;
            material.SerializeTo(materialPath, AssetManager.Serializer);
            WriteMetadataForGameAsset(materialPath, material.ID);

            SubMesh externalSubMesh = new(new SubMeshLOD(material, skinnedMesh, 0.0f))
            {
                Name = "ExternalSkinnedSubMesh",
                RootBone = boneNode.Transform,
                RootTransform = root.Transform,
            };

            string subMeshPath = Path.Combine(assetDirectory, "ExternalSkinnedSubMesh.asset");
            externalSubMesh.FilePath = subMeshPath;
            externalSubMesh.SourceAsset = externalSubMesh;
            externalSubMesh.SerializeTo(subMeshPath, AssetManager.Serializer);
            WriteMetadataForGameAsset(subMeshPath, externalSubMesh.ID);

            string yaml = AssetManager.Serializer.Serialize(root);
            SceneNode clone = AssetManager.Deserializer.Deserialize<SceneNode>(yaml);

            SceneNode cloneMeshNode = clone.FindDescendantByName("MeshNode").ShouldNotBeNull();
            SceneNode cloneBoneNode = clone.FindDescendantByName("BoneA").ShouldNotBeNull();
            XRMesh loadedMesh = Engine.Assets.Load<XRMesh>(meshPath).ShouldNotBeNull();
            loadedMesh.HasSkinning.ShouldBeTrue();
            loadedMesh.HasBlendshapes.ShouldBeTrue();
            SubMesh loadedSubMesh = Engine.Assets.Load<SubMesh>(subMeshPath).ShouldNotBeNull();
            ModelComponent cloneComponent = cloneMeshNode.AddComponent<ModelComponent>().ShouldNotBeNull();

            RenderableMesh renderable = new(loadedSubMesh, cloneComponent);

            XRMesh runtimeMesh = renderable.CurrentLODRenderer!.Mesh.ShouldNotBeNull();
            runtimeMesh.ShouldNotBeSameAs(skinnedMesh);
            runtimeMesh.HasSkinning.ShouldBeTrue();
            runtimeMesh.HasBlendshapes.ShouldBeTrue();
            runtimeMesh.BlendshapeNames.ShouldContain("Smile");
            runtimeMesh.UtilizedBones.Length.ShouldBe(1);
            runtimeMesh.UtilizedBones[0].tfm.ShouldBeSameAs(cloneBoneNode.Transform);
            renderable.RootBone.ShouldBeSameAs(cloneBoneNode.Transform);
        }
        finally
        {
            DeleteDirectoryIfExists(assetDirectory);
            DeleteDirectoryIfExists(metadataDirectory);
            Engine.Assets.MonitorGameAssetsForChanges = previousMonitorSetting;
        }
    }

    [Test]
    public void SyncMetadataWithAssets_ExternalSubMeshReloadsNestedMeshAndMaterialByGeneratedMetadata()
    {
        string tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "PrefabModelSerializationMetadata", Guid.NewGuid().ToString("N"));
        string assetsRoot = Path.Combine(tempRoot, "Assets");
        string metadataRoot = Path.Combine(tempRoot, "Metadata");

        Directory.CreateDirectory(assetsRoot);
        Directory.CreateDirectory(metadataRoot);

        bool previousMonitorSetting = Engine.Assets.MonitorGameAssetsForChanges;
        string previousAssetsPath = Engine.Assets.GameAssetsPath;
        string? previousMetadataPath = Engine.Assets.GameMetadataPath;

        try
        {
            Engine.Assets.MonitorGameAssetsForChanges = false;
            Engine.Assets.GameAssetsPath = assetsRoot;
            Engine.Assets.GameMetadataPath = metadataRoot;
            ClearAssetCaches();

            string meshPath = Path.Combine(assetsRoot, "Meshes", "MetadataRoundTripMesh.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(meshPath).ShouldNotBeNull());
            XRMesh mesh = CreateTriangleMesh("MetadataRoundTripMesh");
            mesh.FilePath = meshPath;
            mesh.SourceAsset = mesh;
            mesh.SerializeTo(meshPath, AssetManager.Serializer);

            string materialPath = Path.Combine(assetsRoot, "Materials", "MetadataRoundTripMaterial.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath).ShouldNotBeNull());
            XRMaterial material = new()
            {
                Name = "MetadataRoundTripMaterial"
            };
            material.FilePath = materialPath;
            material.SourceAsset = material;
            material.SerializeTo(materialPath, AssetManager.Serializer);

            string subMeshPath = Path.Combine(assetsRoot, "SubMeshes", "MetadataRoundTripSubMesh.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(subMeshPath).ShouldNotBeNull());
            SubMesh subMesh = new(new SubMeshLOD(material, mesh, 0.0f))
            {
                Name = "MetadataRoundTripSubMesh"
            };
            subMesh.FilePath = subMeshPath;
            subMesh.SourceAsset = subMesh;
            subMesh.SerializeTo(subMeshPath, AssetManager.Serializer);

            WriteMetadataForGameAsset(meshPath, Guid.NewGuid());
            WriteMetadataForGameAsset(materialPath, Guid.NewGuid());
            WriteMetadataForGameAsset(subMeshPath, Guid.NewGuid());

            Engine.Assets.SyncMetadataWithAssets();

            ReadMetadataForGameAsset(meshPath).Guid.ShouldBe(mesh.ID);
            ReadMetadataForGameAsset(materialPath).Guid.ShouldBe(material.ID);
            ReadMetadataForGameAsset(subMeshPath).Guid.ShouldBe(subMesh.ID);

            ClearAssetCaches();

            SubMesh loadedSubMesh = Engine.Assets.Load<SubMesh>(subMeshPath).ShouldNotBeNull();
            loadedSubMesh.LODs.Count.ShouldBe(1);

            SubMeshLOD loadedLod = loadedSubMesh.LODs.Min.ShouldNotBeNull();
            loadedLod.Material.ShouldNotBeNull();
            loadedLod.Material.ID.ShouldBe(material.ID);
            loadedLod.Mesh.ShouldNotBeNull();
            loadedLod.Mesh.ID.ShouldBe(mesh.ID);
            loadedLod.Mesh.VertexCount.ShouldBe(mesh.VertexCount);
            loadedLod.Mesh.Triangles.ShouldNotBeNull();
            mesh.Triangles.ShouldNotBeNull();
            loadedLod.Mesh.Triangles.Count.ShouldBe(mesh.Triangles.Count);
        }
        finally
        {
            ClearAssetCaches();
            Engine.Assets.GameAssetsPath = previousAssetsPath;
            Engine.Assets.GameMetadataPath = previousMetadataPath;
            Engine.Assets.MonitorGameAssetsForChanges = previousMonitorSetting;
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    [Test]
    public void SyncMetadataWithAssets_PrunesOrphanedAndTemporaryMetadataFiles()
    {
        string tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "PrefabMetadataPrune", Guid.NewGuid().ToString("N"));
        string assetsRoot = Path.Combine(tempRoot, "Assets");
        string metadataRoot = Path.Combine(tempRoot, "Metadata");

        Directory.CreateDirectory(assetsRoot);
        Directory.CreateDirectory(metadataRoot);

        bool previousMonitorSetting = Engine.Assets.MonitorGameAssetsForChanges;
        string previousAssetsPath = Engine.Assets.GameAssetsPath;
        string? previousMetadataPath = Engine.Assets.GameMetadataPath;

        try
        {
            Engine.Assets.MonitorGameAssetsForChanges = false;
            Engine.Assets.GameAssetsPath = assetsRoot;
            Engine.Assets.GameMetadataPath = metadataRoot;
            ClearAssetCaches();

            string modelPath = Path.Combine(assetsRoot, "Models", "LiveModel.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath).ShouldNotBeNull());

            Model model = CreateTriangleModel(modelName: "LiveModel", subMeshName: "LiveSubMesh");
            model.FilePath = modelPath;
            model.SourceAsset = model;
            model.SerializeTo(modelPath, AssetManager.Serializer);

            WriteMetadataForGameAsset(modelPath, Guid.NewGuid());

            string orphanMetaPath = Path.Combine(metadataRoot, "Models", "MissingModel.asset.meta");
            Directory.CreateDirectory(Path.GetDirectoryName(orphanMetaPath).ShouldNotBeNull());
            File.WriteAllText(orphanMetaPath, AssetManager.Serializer.Serialize(new AssetMetadata
            {
                Guid = Guid.NewGuid(),
                Name = "MissingModel.asset",
                RelativePath = "Models/MissingModel.asset",
                IsDirectory = false,
                LastSyncedUtc = DateTime.UtcNow,
            }));

            string tempMetaPath = Path.Combine(metadataRoot, "Models", "LiveModel.asset.123.tmp.meta");
            File.WriteAllText(tempMetaPath, AssetManager.Serializer.Serialize(new AssetMetadata
            {
                Guid = Guid.NewGuid(),
                Name = "LiveModel.asset.123.tmp",
                RelativePath = "Models/LiveModel.asset.123.tmp",
                IsDirectory = false,
                LastSyncedUtc = DateTime.UtcNow,
            }));

            Engine.Assets.SyncMetadataWithAssets();

            ReadMetadataForGameAsset(modelPath).Guid.ShouldBe(model.ID);
            File.Exists(orphanMetaPath).ShouldBeFalse();
            File.Exists(tempMetaPath).ShouldBeFalse();
        }
        finally
        {
            ClearAssetCaches();
            Engine.Assets.GameAssetsPath = previousAssetsPath;
            Engine.Assets.GameMetadataPath = previousMetadataPath;
            Engine.Assets.MonitorGameAssetsForChanges = previousMonitorSetting;
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    private static SceneNode CreateSceneNodeWithModel(Model model)
    {
        SceneNode node = new("PrefabModelRoot");
        ModelComponent component = node.AddComponent<ModelComponent>().ShouldNotBeNull();
        component.Model = model;
        return node;
    }

    private static Model CreateTriangleModel(string modelName, string subMeshName)
    {
        XRMesh mesh = CreateTriangleMesh($"{subMeshName}_Mesh");

        XRMaterial material = new()
        {
            Name = $"{subMeshName}_Material"
        };

        SubMesh subMesh = new(new SubMeshLOD(material, mesh, 0.0f))
        {
            Name = subMeshName
        };

        return new Model(subMesh)
        {
            Name = modelName
        };
    }

    private static XRMesh CreateTriangleMesh(string meshName)
    {
        XRMesh mesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        mesh.Name = meshName;
        return mesh;
    }

    private static XRMesh CreateSkinnedBlendshapeMesh(string meshName, TransformBase bone, string blendshapeName)
    {
        Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> CreateWeights()
            => new(System.Collections.Generic.ReferenceEqualityComparer.Instance)
            {
                [bone] = (1.0f, Matrix4x4.Identity)
            };

        Vertex CreateVertex(Vector3 position, float morphOffsetY)
            => new(position)
            {
                Weights = CreateWeights(),
                Blendshapes =
                [
                    (blendshapeName, new XREngine.Data.Rendering.VertexData
                    {
                        Position = new Vector3(position.X, position.Y + morphOffsetY, position.Z),
                        Normal = Vector3.UnitZ,
                    })
                ],
                Normal = Vector3.UnitZ,
            };

        List<Vertex> vertices =
        [
            CreateVertex(new Vector3(0.0f, 0.0f, 0.0f), 0.25f),
            CreateVertex(new Vector3(1.0f, 0.0f, 0.0f), 0.10f),
            CreateVertex(new Vector3(0.0f, 1.0f, 0.0f), 0.15f),
        ];

        XRMesh mesh = new(vertices, [0, 1, 2])
        {
            Name = meshName,
        };
        mesh.RebuildSkinningBuffersFromVertices();
        mesh.BlendshapeNames = [blendshapeName];
        mesh.RebuildBlendshapeBuffersFromVertices();
        return mesh;
    }

    private static void AssertNodeModelShape(SceneNode node, string expectedSubMeshName)
    {
        ModelComponent component = node.GetComponent<ModelComponent>().ShouldNotBeNull();
        component.Meshes.Count.ShouldBe(1);
        AssertModelShape(component.Model, expectedSubMeshName);
    }

    private static void AssertModelShape(Model? model, string expectedSubMeshName)
    {
        model.ShouldNotBeNull();
        model!.Meshes.Count.ShouldBe(1);

        SubMesh subMesh = model.Meshes[0].ShouldNotBeNull();
        subMesh.Name.ShouldBe(expectedSubMeshName);
        subMesh.LODs.Count.ShouldBe(1);

        SubMeshLOD lod = subMesh.LODs.Min.ShouldNotBeNull();
        lod.Mesh.ShouldNotBeNull();
        lod.Material.ShouldNotBeNull();
    }

    private static void WriteMetadataForGameAsset(string assetPath, Guid assetId)
    {
        string assetsRoot = Engine.Assets.GameAssetsPath;
        string metadataRoot = Engine.Assets.GameMetadataPath.ShouldNotBeNull();
        string relativePath = Path.GetRelativePath(assetsRoot, assetPath);
        string metadataPath = Path.Combine(metadataRoot, relativePath) + ".meta";

        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath).ShouldNotBeNull());

        AssetMetadata metadata = new()
        {
            Guid = assetId,
            Name = Path.GetFileName(assetPath),
            RelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            IsDirectory = false,
            LastSyncedUtc = DateTime.UtcNow,
        };

        File.WriteAllText(metadataPath, AssetManager.Serializer.Serialize(metadata));
    }

    private static AssetMetadata ReadMetadataForGameAsset(string assetPath)
    {
        string assetsRoot = Engine.Assets.GameAssetsPath;
        string metadataRoot = Engine.Assets.GameMetadataPath.ShouldNotBeNull();
        string relativePath = Path.GetRelativePath(assetsRoot, assetPath);
        string metadataPath = Path.Combine(metadataRoot, relativePath) + ".meta";

        File.Exists(metadataPath).ShouldBeTrue();
        return AssetManager.Deserializer.Deserialize<AssetMetadata>(File.ReadAllText(metadataPath)).ShouldNotBeNull();
    }

    private static void ClearAssetCaches()
    {
        Engine.Assets.LoadedAssetsByPathInternal.Clear();
        Engine.Assets.LoadedAssetsByOriginalPathInternal.Clear();
        Engine.Assets.LoadedAssetsByIDInternal.Clear();
    }

    private static void DeleteDirectoryIfExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return;

        foreach (string filePath in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            if (!File.Exists(filePath))
                continue;

            File.SetAttributes(filePath, FileAttributes.Normal);
        }

        foreach (string childDirectory in Directory.GetDirectories(directoryPath, "*", SearchOption.AllDirectories))
        {
            if (!Directory.Exists(childDirectory))
                continue;

            File.SetAttributes(childDirectory, FileAttributes.Normal);
        }

        File.SetAttributes(directoryPath, FileAttributes.Normal);
        Directory.Delete(directoryPath, recursive: true);
    }
}