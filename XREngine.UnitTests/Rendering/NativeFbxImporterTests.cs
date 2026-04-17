using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;
using Assimp;
using NUnit.Framework;
using Shouldly;
using XREngine.Fbx;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Generator;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class NativeFbxImporterTests
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new TestRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousServices;
    }

    [Test]
    public void Import_NativeFbxBackend_BuildsStaticSceneHierarchyAndMaterials()
    {
        string tempDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"fbx-native-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        string texturePath = Path.Combine(tempDirectory, "albedo.png");
        File.WriteAllBytes(texturePath, []);

        string fbxPath = Path.Combine(tempDirectory, "quad.fbx");
        File.WriteAllText(fbxPath, CreateSyntheticFbx(Path.GetFileName(texturePath)));

        try
        {
            using var importer = new ModelImporter(fbxPath, onCompleted: null, materialFactory: null)
            {
                ImportOptions = new ModelImportOptions
                {
                    GenerateMeshRenderersAsync = false,
                },
                MakeTextureAction = static path => new XRTexture2D
                {
                    FilePath = path,
                    Name = Path.GetFileNameWithoutExtension(path),
                },
            };

            SceneNode? rootNode = importer.Import(PostProcessSteps.None, onProgress: null);

            rootNode.ShouldNotBeNull();
            rootNode!.Name.ShouldBe("quad");

            SceneNode? meshNode = rootNode.FindDescendantByName("MeshNode");
            meshNode.ShouldNotBeNull();

            ModelComponent? component = meshNode!.GetComponent<ModelComponent>();
            component.ShouldNotBeNull();
            component!.Model.ShouldNotBeNull();
            component.Model!.Meshes.Count.ShouldBe(1);

            SubMesh subMesh = component.Model.Meshes[0];
            subMesh.Name.ShouldBe("Quad");

            SubMeshLOD lod = subMesh.LODs.Min!;
            lod.GenerateAsync.ShouldBeFalse();
            lod.Mesh.ShouldNotBeNull();
            lod.Material.ShouldNotBeNull();

            lod.Mesh!.Bounds.Max.X.ShouldBe(1.0f);
            lod.Mesh.Bounds.Max.Y.ShouldBe(1.0f);
            lod.Mesh.Vertices.Length.ShouldBe(4);
            lod.Mesh.GetIndices()?.Length.ShouldBe(6);

            lod.Material!.Name.ShouldBe("Stone");
            lod.Material.Textures.Count.ShouldBe(1);
            ((XRTexture2D)lod.Material.Textures[0]!).FilePath.ShouldEndWith("albedo.png");
            lod.Material.FragmentShaders.ShouldNotBeEmpty();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Import_NativeFbxBackend_IsDefaultWhenImportOptionsAreNotProvided()
    {
        string tempDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"fbx-native-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        string fbxPath = Path.Combine(tempDirectory, "phase4-default.fbx");
        File.WriteAllText(fbxPath, CreateSyntheticPhase4Fbx());

        try
        {
            using var importer = new ModelImporter(fbxPath, onCompleted: null, materialFactory: null);

            SceneNode? rootNode = importer.Import(PostProcessSteps.None, onProgress: null);

            rootNode.ShouldNotBeNull();
            rootNode!.Name.ShouldBe("phase4-default");

            SceneNode? meshNode = rootNode.FindDescendantByName("MeshNode");
            meshNode.ShouldNotBeNull();

            ModelComponent? modelComponent = meshNode!.GetComponent<ModelComponent>();
            modelComponent.ShouldNotBeNull();
            modelComponent!.Model.ShouldNotBeNull();
            modelComponent.Model.Meshes.Count.ShouldBe(1);

            XRMesh mesh = modelComponent.Model.Meshes[0].LODs.Min!.Mesh!;
            mesh.HasSkinning.ShouldBeTrue();
            mesh.HasBlendshapes.ShouldBeTrue();

            AnimationClipComponent? clipComponent = rootNode.GetComponent<AnimationClipComponent>();
            clipComponent.ShouldNotBeNull();
            clipComponent!.Animation.ShouldNotBeNull();
            clipComponent.Animation!.Name.ShouldBe("Take 001");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Import_NativeFbxBackend_HonorsFlipUvsPostProcessSetting()
    {
        string tempDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"fbx-native-flipuv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        string fbxPath = Path.Combine(tempDirectory, "quad-uvs.fbx");
        File.WriteAllText(fbxPath, CreateSyntheticFbx("albedo.png"));

        try
        {
            static XRMesh ImportMesh(string sourcePath, ModelImportOptions options)
            {
                using var importer = new ModelImporter(sourcePath, onCompleted: null, materialFactory: null)
                {
                    ImportOptions = options,
                };

                SceneNode? rootNode = importer.Import(PostProcessSteps.None, onProgress: null);
                rootNode.ShouldNotBeNull();

                ModelComponent? component = rootNode!.FindDescendantByName("MeshNode")?.GetComponent<ModelComponent>();
                component.ShouldNotBeNull();
                component!.Model.ShouldNotBeNull();
                return component.Model!.Meshes[0].LODs.Min!.Mesh!;
            }

            XRMesh flippedMesh = ImportMesh(fbxPath, new ModelImportOptions
            {
                GenerateMeshRenderersAsync = false,
            });

            flippedMesh.Vertices[0].TextureCoordinateSets.ShouldNotBeNull();
            flippedMesh.Vertices[0].TextureCoordinateSets![0].X.ShouldBe(0.0f, 0.0001f);
            flippedMesh.Vertices[0].TextureCoordinateSets![0].Y.ShouldBe(1.0f, 0.0001f);
            flippedMesh.Vertices[2].TextureCoordinateSets![0].X.ShouldBe(1.0f, 0.0001f);
            flippedMesh.Vertices[2].TextureCoordinateSets![0].Y.ShouldBe(0.0f, 0.0001f);

            XRMesh unflippedMesh = ImportMesh(fbxPath, new ModelImportOptions
            {
                GenerateMeshRenderersAsync = false,
                LegacyPostProcessSteps = PostProcessSteps.None,
            });

            unflippedMesh.Vertices[0].TextureCoordinateSets.ShouldNotBeNull();
            unflippedMesh.Vertices[0].TextureCoordinateSets![0].X.ShouldBe(0.0f, 0.0001f);
            unflippedMesh.Vertices[0].TextureCoordinateSets![0].Y.ShouldBe(0.0f, 0.0001f);
            unflippedMesh.Vertices[2].TextureCoordinateSets![0].X.ShouldBe(1.0f, 0.0001f);
            unflippedMesh.Vertices[2].TextureCoordinateSets![0].Y.ShouldBe(1.0f, 0.0001f);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Import_NativeFbxBackend_MissingTexturePaths_DoNotRequirePreviewJobScheduling()
    {
        string tempDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"fbx-native-missing-texture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        string fbxPath = Path.Combine(tempDirectory, "quad-missing-texture.fbx");
        File.WriteAllText(fbxPath, CreateSyntheticFbx("missing-albedo.png"));

        try
        {
            using var importer = new ModelImporter(fbxPath, onCompleted: null, materialFactory: null)
            {
                ImportOptions = new ModelImportOptions
                {
                    GenerateMeshRenderersAsync = false,
                },
            };

            SceneNode? rootNode = null;
            Should.NotThrow(() => rootNode = importer.Import(PostProcessSteps.None, onProgress: null));

            rootNode.ShouldNotBeNull();
            ModelComponent? component = rootNode!.FindDescendantByName("MeshNode")?.GetComponent<ModelComponent>();
            component.ShouldNotBeNull();

            XRMaterial material = component!.Model!.Meshes[0].LODs.Min!.Material!;
            material.Textures.Count.ShouldBe(1);
            XRTexture2D placeholder = material.Textures[0].ShouldBeOfType<XRTexture2D>();
            placeholder.FilePath.ShouldEndWith("missing-albedo.png");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void PrefabSource_SynchronousMeshImportScope_ForcesFalseAndRestoresRequestedValue()
    {
        ModelImportOptions options = new()
        {
            ProcessMeshesAsynchronously = true,
        };

        using (XRPrefabSource.EnterSynchronousMeshImportScope(options))
        {
            options.ProcessMeshesAsynchronously.ShouldBe(false);
        }

        options.ProcessMeshesAsynchronously.ShouldBe(true);

        options.ProcessMeshesAsynchronously = null;

        using (XRPrefabSource.EnterSynchronousMeshImportScope(options))
        {
            options.ProcessMeshesAsynchronously.ShouldBe(false);
        }

        options.ProcessMeshesAsynchronously.ShouldBeNull();

    }

    [Test]
    public void Import_NativeFbxBackend_ImportsSkinningBlendshapesAndAnimation()
    {
        string tempDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"fbx-native-phase4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        string fbxPath = Path.Combine(tempDirectory, "phase4.fbx");
        File.WriteAllText(fbxPath, CreateSyntheticPhase4Fbx());

        try
        {
            using var importer = new ModelImporter(fbxPath, onCompleted: null, materialFactory: null)
            {
                ImportOptions = new ModelImportOptions
                {
                    GenerateMeshRenderersAsync = false,
                },
            };

            SceneNode? rootNode = importer.Import(PostProcessSteps.None, onProgress: null);

            rootNode.ShouldNotBeNull();
            rootNode!.Name.ShouldBe("phase4");

            SceneNode? meshNode = rootNode.FindDescendantByName("MeshNode");
            SceneNode? boneA = rootNode.FindDescendantByName("BoneA");
            SceneNode? boneB = rootNode.FindDescendantByName("BoneB");
            meshNode.ShouldNotBeNull();
            boneA.ShouldNotBeNull();
            boneB.ShouldNotBeNull();

            ModelComponent? modelComponent = meshNode!.GetComponent<ModelComponent>();
            modelComponent.ShouldNotBeNull();
            SubMeshLOD lod = modelComponent!.Model!.Meshes[0].LODs.Min!;
            XRMesh mesh = lod.Mesh!;

            mesh.HasSkinning.ShouldBeTrue();
            mesh.HasBlendshapes.ShouldBeTrue();
            mesh.UtilizedBones.Length.ShouldBe(2);
            mesh.UtilizedBones.Select(static entry => entry.tfm.Name).ShouldContain("BoneA");
            mesh.UtilizedBones.Select(static entry => entry.tfm.Name).ShouldContain("BoneB");
            mesh.BlendshapeNames.ShouldContain("Smile");

            Vertex weightedVertex = mesh.Vertices[2];
            weightedVertex.Weights.ShouldNotBeNull();
            weightedVertex.Weights!.Count.ShouldBe(2);
            weightedVertex.Weights.Keys.Select(static bone => bone.Name).ShouldContain("BoneA");
            weightedVertex.Weights.Keys.Select(static bone => bone.Name).ShouldContain("BoneB");
            weightedVertex.Weights.Values.Sum(static weight => weight.weight).ShouldBe(1.0f, 0.0001f);

            Vertex morphedVertex = mesh.Vertices[0];
            morphedVertex.Blendshapes.ShouldNotBeNull();
            morphedVertex.Blendshapes!.Count.ShouldBe(1);
            morphedVertex.Blendshapes[0].name.ShouldBe("Smile");
            morphedVertex.Blendshapes[0].data.Position.X.ShouldBe(0.0f, 0.0001f);
            morphedVertex.Blendshapes[0].data.Position.Y.ShouldBe(0.25f, 0.0001f);
            morphedVertex.Blendshapes[0].data.Position.Z.ShouldBe(0.0f, 0.0001f);

            AnimationClipComponent? clipComponent = rootNode.GetComponent<AnimationClipComponent>();
            clipComponent.ShouldNotBeNull();
            clipComponent!.Animation.ShouldNotBeNull();
            clipComponent.Animation!.Name.ShouldBe("Take 001");
            clipComponent.Animation.ClipKind.ShouldBe(EAnimationClipKind.GenericTransform);

            AnimationMember rootMember = clipComponent.Animation.RootMember!;
            AnimationMember sceneNodeMember = FindChild(rootMember, "SceneNode", EAnimationMemberType.Property);
            AnimationMember authoredRootSearch = FindChild(sceneNodeMember, "FindDescendantByName", EAnimationMemberType.Method, "Root");
            AnimationMember boneSearch = FindChild(authoredRootSearch, "FindDescendantByName", EAnimationMemberType.Method, "BoneB");
            AnimationMember transformMember = FindChild(boneSearch, "Transform", EAnimationMemberType.Property);
            AnimationMember translationX = FindChild(transformMember, "TranslationX", EAnimationMemberType.Property);
            translationX.Animation.ShouldNotBeNull();
            ((PropAnimFloat)translationX.Animation!).Keyframes.Count.ShouldBe(2);

            AnimationMember meshSearch = FindChild(authoredRootSearch, "FindDescendantByName", EAnimationMemberType.Method, "MeshNode");
            AnimationMember getComponent = FindChild(meshSearch, "GetComponent", EAnimationMemberType.Method, "ModelComponent");
            AnimationMember blendshapeMethod = FindChild(getComponent, "SetBlendShapeWeightNormalized", EAnimationMemberType.Method, "Smile");
            blendshapeMethod.Animation.ShouldNotBeNull();
            ((PropAnimFloat)blendshapeMethod.Animation!).Keyframes.Count.ShouldBe(2);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void XRMesh_RebuildBlendshapeBuffersFromVertices_AllowsSparsePerVertexBlendshapeLists()
    {
        Vertex[] vertices =
        [
            new Vertex(new Vector3(0.0f, 0.0f, 0.0f))
            {
                Blendshapes =
                [
                    ("Smile", new VertexData
                    {
                        Position = new Vector3(0.0f, 0.10f, 0.0f),
                    })
                ]
            },
            new Vertex(new Vector3(1.0f, 0.0f, 0.0f))
            {
                Blendshapes =
                [
                    ("Blink", new VertexData
                    {
                        Position = new Vector3(1.0f, 0.20f, 0.0f),
                    })
                ]
            },
            new Vertex(new Vector3(0.0f, 1.0f, 0.0f)),
        ];

        XRMesh mesh = new(vertices, new List<ushort> { 0, 1, 2 })
        {
            BlendshapeNames = ["Smile", "Blink"],
        };

        Should.NotThrow(() => mesh.RebuildBlendshapeBuffersFromVertices());
        mesh.HasBlendshapes.ShouldBeTrue();
        mesh.BlendshapeCounts.ShouldNotBeNull();
        mesh.BlendshapeCounts!.ElementCount.ShouldBe((uint)vertices.Length);
        mesh.BlendshapeIndices.ShouldNotBeNull();
        mesh.BlendshapeIndices!.ElementCount.ShouldBe(2u);
        mesh.BlendshapeDeltas.ShouldNotBeNull();
        mesh.BlendshapeDeltas!.ElementCount.ShouldBe(3u);
    }

    [Test]
    public void ModelImportOptions_LegacyFbxYamlAliases_MapToNativeProperties()
    {
        const string yaml = """
            PreservePivots: false
            RemoveAssimpFBXNodes: false
            """;

        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        ModelImportOptions options = deserializer.Deserialize<ModelImportOptions>(yaml);

        options.FbxPivotPolicy.ShouldBe(FbxPivotImportPolicy.BakeIntoLocalTransform);
        options.CollapseGeneratedFbxHelperNodes.ShouldBeFalse();
    }

    [Test]
    public void NativeFbxSkinWeights_UseImportedMeshBindPoseInsteadOfClusterTransformMatrix()
    {
        SceneNode rootNode = new("Root");
        SceneNode meshNode = new(rootNode, "MeshNode");
        SceneNode boneNode = new(rootNode, "Bone");

        SetLocalMatrix(meshNode, Matrix4x4.CreateRotationZ(0.35f) * Matrix4x4.CreateTranslation(3.0f, -2.0f, 1.0f));
        SetLocalMatrix(boneNode, Matrix4x4.CreateRotationX(-0.5f) * Matrix4x4.CreateTranslation(6.0f, 4.0f, -3.0f));

        const long boneObjectId = 2001;
        Matrix4x4 importedMeshWorld = meshNode.Transform.WorldMatrix;
        Matrix4x4 importedBoneWorld = boneNode.Transform.WorldMatrix;
        Matrix4x4 inverseBoneWorld = Matrix4x4.Invert(importedBoneWorld, out Matrix4x4 boneWorldInverse)
            ? boneWorldInverse
            : Matrix4x4.Identity;
        Matrix4x4 clusterTransformMatrix = Matrix4x4.CreateTranslation(-7.0f, 5.0f, 9.0f);

        FbxClusterBinding cluster = new(
            ClusterObjectId: 1001,
            BoneModelObjectId: boneObjectId,
            BoneName: "Bone",
            TransformMatrix: clusterTransformMatrix,
            TransformLinkMatrix: importedBoneWorld,
            InverseBindMatrix: clusterTransformMatrix * inverseBoneWorld,
            ControlPointWeights: new Dictionary<int, float> { [0] = 1.0f });
        FbxSkinBinding skinBinding = new(
            GeometryObjectId: 3001,
            SkinObjectId: 3002,
            Name: "Skin",
            Clusters: new[] { cluster });

        Dictionary<long, SceneNode> nodesByObjectId = new()
        {
            [boneObjectId] = boneNode,
        };

        Type importerType = typeof(ModelImporter).Assembly.GetType("XREngine.NativeFbxSceneImporter", throwOnError: true)!;
        MethodInfo method = importerType.GetMethod("BuildSkinWeightsByControlPoint", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (Dictionary<int, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>>)method.Invoke(
            obj: null,
            parameters: new object[] { skinBinding, importedMeshWorld, nodesByObjectId })!;

        result.ShouldContainKey(0);
        result[0].ShouldContainKey(boneNode.Transform);

        (float weight, Matrix4x4 bindInvWorldMatrix) weightData = result[0][boneNode.Transform];
        weightData.weight.ShouldBe(1.0f, 0.0001f);

        Matrix4x4 expectedInvBind = importedMeshWorld * inverseBoneWorld;
        MatrixShouldBe(weightData.bindInvWorldMatrix, expectedInvBind, 0.0001f);

        Vector3 meshLocalPosition = new(1.0f, 2.0f, -0.5f);
        Vector3 expectedWorldPosition = Vector3.Transform(meshLocalPosition, importedMeshWorld);
        Vector3 skinnedWorldPosition = Vector3.Transform(meshLocalPosition, weightData.bindInvWorldMatrix * importedBoneWorld);
        Vector3.Distance(skinnedWorldPosition, expectedWorldPosition).ShouldBeLessThan(0.0001f);
    }

    [Test]
    public void DefaultVertexShaderGenerator_SkinningUsesRowVectorMultiplication()
    {
        SceneNode boneNode = new("Bone");

        Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> CreateWeights()
            => new()
            {
                [boneNode.Transform] = (1.0f, Matrix4x4.Identity),
            };

        Vertex[] vertices =
        [
            new Vertex(new Vector3(0.0f, 0.0f, 0.0f))
            {
                Normal = Vector3.UnitZ,
                Tangent = Vector3.UnitX,
                Weights = CreateWeights(),
            },
            new Vertex(new Vector3(1.0f, 0.0f, 0.0f))
            {
                Normal = Vector3.UnitZ,
                Tangent = Vector3.UnitX,
                Weights = CreateWeights(),
            },
            new Vertex(new Vector3(0.0f, 1.0f, 0.0f))
            {
                Normal = Vector3.UnitZ,
                Tangent = Vector3.UnitX,
                Weights = CreateWeights(),
            },
        ];

        XRMesh mesh = new(vertices, new List<ushort> { 0, 1, 2 });
        mesh.RebuildSkinningBuffersFromVertices();
        mesh.SkinningShaderConvention.ShouldBe(ESkinningShaderConvention.ExplicitRowMajorRowVector);

        string source = new DefaultVertexShaderGenerator(mesh).Generate();

        source.ShouldContain("layout(row_major, std430, binding = 0) buffer BoneMatricesBuffer");
        source.ShouldContain("layout(row_major, std430, binding = 1) buffer BoneInvBindMatricesBuffer");
        source.ShouldContain($"mat4 boneMatrix = {ECommonBufferType.BoneInvBindMatrices}[paletteIndex] * {ECommonBufferType.BoneMatrices}[paletteIndex];");
        source.ShouldContain($"{DefaultVertexShaderGenerator.FinalPositionName} += (vec4({DefaultVertexShaderGenerator.BasePositionName}, 1.0f) * boneMatrix) * weight;");
        source.ShouldContain($"{DefaultVertexShaderGenerator.FinalNormalName} += ({DefaultVertexShaderGenerator.BaseNormalName} * boneMatrix3) * weight;");
        source.ShouldContain($"{DefaultVertexShaderGenerator.FinalTangentName} += ({DefaultVertexShaderGenerator.BaseTangentName} * boneMatrix3) * weight;");
        source.ShouldNotContain($"boneMatrix * vec4({DefaultVertexShaderGenerator.BasePositionName}, 1.0f)");
        source.ShouldNotContain($"boneMatrix3 * {DefaultVertexShaderGenerator.BaseNormalName}");
        source.ShouldNotContain($"boneMatrix3 * {DefaultVertexShaderGenerator.BaseTangentName}");
    }

    [Test]
    public void DefaultVertexShaderGenerator_LegacySkinningUsesCompatibilityContract()
    {
        SceneNode boneNode = new("Bone");

        Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> CreateWeights()
            => new()
            {
                [boneNode.Transform] = (1.0f, Matrix4x4.Identity),
            };

        Vertex[] vertices =
        [
            new Vertex(new Vector3(0.0f, 0.0f, 0.0f))
            {
                Normal = Vector3.UnitZ,
                Tangent = Vector3.UnitX,
                Weights = CreateWeights(),
            },
            new Vertex(new Vector3(1.0f, 0.0f, 0.0f))
            {
                Normal = Vector3.UnitZ,
                Tangent = Vector3.UnitX,
                Weights = CreateWeights(),
            },
            new Vertex(new Vector3(0.0f, 1.0f, 0.0f))
            {
                Normal = Vector3.UnitZ,
                Tangent = Vector3.UnitX,
                Weights = CreateWeights(),
            },
        ];

        XRMesh mesh = new(vertices, new List<ushort> { 0, 1, 2 });
        mesh.RebuildSkinningBuffersFromVertices();
        mesh.SkinningShaderConvention = ESkinningShaderConvention.LegacyImplicitTranspose;

        string source = new DefaultVertexShaderGenerator(mesh).Generate();

        source.ShouldContain("layout(std430, binding = 0) buffer BoneMatricesBuffer");
        source.ShouldContain("layout(std430, binding = 1) buffer BoneInvBindMatricesBuffer");
        source.ShouldNotContain("layout(row_major, std430, binding = 0) buffer BoneMatricesBuffer");
        source.ShouldNotContain("layout(row_major, std430, binding = 1) buffer BoneInvBindMatricesBuffer");
        source.ShouldContain($"mat4 boneMatrix = {ECommonBufferType.BoneMatrices}[paletteIndex] * {ECommonBufferType.BoneInvBindMatrices}[paletteIndex];");
        source.ShouldContain($"{DefaultVertexShaderGenerator.FinalPositionName} += (boneMatrix * vec4({DefaultVertexShaderGenerator.BasePositionName}, 1.0f)) * weight;");
        source.ShouldContain($"{DefaultVertexShaderGenerator.FinalNormalName} += (boneMatrix3 * {DefaultVertexShaderGenerator.BaseNormalName}) * weight;");
        source.ShouldContain($"{DefaultVertexShaderGenerator.FinalTangentName} += (boneMatrix3 * {DefaultVertexShaderGenerator.BaseTangentName}) * weight;");
        source.ShouldNotContain($"{DefaultVertexShaderGenerator.FinalPositionName} += (vec4({DefaultVertexShaderGenerator.BasePositionName}, 1.0f) * boneMatrix) * weight;");
    }

    private static string CreateSyntheticFbx(string relativeTexturePath)
        =>
            """
            ; FBX 7.4.0 project file
            Definitions:  {
                Version: 100
                Count: 5
                ObjectType: "Model" {
                    Count: 2
                }
                ObjectType: "Geometry" {
                    Count: 1
                }
                ObjectType: "Material" {
                    Count: 1
                }
                ObjectType: "Texture" {
                    Count: 1
                }
                ObjectType: "Video" {
                    Count: 1
                }
            }
            Objects:  {
                Model: 1000, "Model::Root", "Null" {
                    Properties70:  {
                        P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0
                    }
                }
                Model: 1001, "Model::MeshNode", "Mesh" {
                    Properties70:  {
                        P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0
                        P: "GeometricTranslation", "Vector3D", "Vector", "",0,0,0
                    }
                }
                Geometry: 2000, "Geometry::Quad", "Mesh" {
                    Vertices: *12 {
                        a: 0,0,0,1,0,0,1,1,0,0,1,0
                    }
                    PolygonVertexIndex: *4 {
                        a: 0,1,2,-4
                    }
                    LayerElementNormal: 0 {
                        Version: 101
                        Name: ""
                        MappingInformationType: "ByPolygonVertex"
                        ReferenceInformationType: "Direct"
                        Normals: *12 {
                            a: 0,0,1,0,0,1,0,0,1,0,0,1
                        }
                    }
                    LayerElementUV: 0 {
                        Version: 101
                        Name: "UVSet0"
                        MappingInformationType: "ByPolygonVertex"
                        ReferenceInformationType: "Direct"
                        UV: *8 {
                            a: 0,0,1,0,1,1,0,1
                        }
                    }
                    LayerElementMaterial: 0 {
                        Version: 101
                        Name: ""
                        MappingInformationType: "AllSame"
                        ReferenceInformationType: "IndexToDirect"
                        Materials: *1 {
                            a: 0
                        }
                    }
                }
                Material: 3000, "Material::Stone", "" {
                    Properties70:  {
                        P: "DiffuseColor", "Color", "", "A",0.7,0.6,0.5
                        P: "Opacity", "Number", "", "A",1
                    }
                }
                Texture: 4000, "Texture::StoneColor", "" {
                    Type: "TextureVideoClip"
                    RelativeFilename: "__TEXTURE__"
                }
                Video: 5000, "Video::StoneColorPng", "Clip" {
                    Type: "Clip"
                    RelativeFilename: "__TEXTURE__"
                }
            }
            Connections:  {
                C: "OO",1000,0
                C: "OO",1001,1000
                C: "OO",2000,1001
                C: "OO",3000,1001
                C: "OP",4000,3000,"DiffuseColor"
                C: "OO",5000,4000
            }
            """.Replace("__TEXTURE__", relativeTexturePath, StringComparison.Ordinal);

    private static string CreateSyntheticPhase4Fbx()
        =>
            """
            ; FBX 7.4.0 project file
            Definitions:  {
                Version: 100
            }
            Objects:  {
                Model: 1000, "Model::Root", "Null" {
                    Properties70:  {
                        P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0
                    }
                }
                Model: 1001, "Model::MeshNode", "Mesh" {
                    Properties70:  {
                        P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0
                        P: "Lcl Rotation", "Lcl Rotation", "", "A",0,0,0
                        P: "Lcl Scaling", "Lcl Scaling", "", "A",1,1,1
                    }
                }
                Model: 1100, "Model::BoneA", "LimbNode" {
                    Properties70:  {
                        P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0
                    }
                }
                Model: 1101, "Model::BoneB", "LimbNode" {
                    Properties70:  {
                        P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0
                    }
                }
                Geometry: 2000, "Geometry::Quad", "Mesh" {
                    Vertices: *12 {
                        a: 0,0,0,1,0,0,1,1,0,0,1,0
                    }
                    PolygonVertexIndex: *4 {
                        a: 0,1,2,-4
                    }
                    GeometryVersion: 124
                    LayerElementNormal: 0 {
                        Version: 101
                        Name: ""
                        MappingInformationType: "ByPolygonVertex"
                        ReferenceInformationType: "Direct"
                        Normals: *12 {
                            a: 0,0,1,0,0,1,0,0,1,0,0,1
                        }
                    }
                    Layer: 0 {
                        Version: 100
                        LayerElement:  {
                            Type: "LayerElementNormal"
                            TypedIndex: 0
                        }
                    }
                }
                Deformer: 6000, "Deformer::Skin", "Skin" {
                }
                Deformer: 6001, "SubDeformer::ClusterA", "Cluster" {
                    Indexes: *4 {
                        a: 0,1,2,3
                    }
                    Weights: *4 {
                        a: 1,1,0.25,0.25
                    }
                    Transform: *16 {
                        a: 1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1
                    }
                    TransformLink: *16 {
                        a: 1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1
                    }
                }
                Deformer: 6002, "SubDeformer::ClusterB", "Cluster" {
                    Indexes: *2 {
                        a: 2,3
                    }
                    Weights: *2 {
                        a: 0.75,0.75
                    }
                    Transform: *16 {
                        a: 1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1
                    }
                    TransformLink: *16 {
                        a: 1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1
                    }
                }
                Deformer: 7000, "Deformer::BlendShape", "BlendShape" {
                }
                Deformer: 7001, "Deformer::Smile", "BlendShapeChannel" {
                    DeformPercent: 25
                    FullWeights: *1 {
                        a: 100
                    }
                }
                Geometry: 8000, "Geometry::SmileShape", "Shape" {
                    PolygonVertexIndex: *4 {
                        a: 0,1,2,-4
                    }
                    Indexes: *4 {
                        a: 0,1,2,3
                    }
                    Vertices: *12 {
                        a: 0,0.25,0,0,0.10,0,0,0.10,0,0,0.25,0
                    }
                }
                AnimationStack: 9000, "AnimStack::Take 001", "" {
                }
                AnimationLayer: 9100, "AnimLayer::BaseLayer", "" {
                }
                AnimationCurveNode: 9200, "AnimCurveNode::BoneBTranslation", "" {
                }
                AnimationCurve: 9201, "AnimCurve::BoneBTranslationX", "" {
                    KeyTime: *2 {
                        a: 0,46186158000
                    }
                    KeyValueFloat: *2 {
                        a: 0,5
                    }
                }
                AnimationCurveNode: 9300, "AnimCurveNode::SmileWeight", "" {
                }
                AnimationCurve: 9301, "AnimCurve::SmileWeight", "" {
                    KeyTime: *2 {
                        a: 0,46186158000
                    }
                    KeyValueFloat: *2 {
                        a: 25,100
                    }
                }
            }
            Connections:  {
                C: "OO",1000,0
                C: "OO",1001,1000
                C: "OO",1100,1000
                C: "OO",1101,1000
                C: "OO",2000,1001
                C: "OO",6000,2000
                C: "OO",6001,6000
                C: "OO",6002,6000
                C: "OO",1100,6001
                C: "OO",1101,6002
                C: "OO",7000,2000
                C: "OO",7001,7000
                C: "OO",8000,7001
                C: "OO",9100,9000
                C: "OO",9200,9100
                C: "OP",9201,9200,"d|X"
                C: "OP",9200,1101,"Lcl Translation"
                C: "OO",9300,9100
                C: "OP",9301,9300,"DeformPercent"
                C: "OP",9300,7001,"DeformPercent"
            }
            """;

    private static AnimationMember FindChild(AnimationMember parent, string memberName, EAnimationMemberType memberType, object? firstArgument = null)
    {
        AnimationMember? child = parent.Children.FirstOrDefault(candidate =>
            candidate.MemberName == memberName
            && candidate.MemberType == memberType
            && (firstArgument is null || (candidate.MethodArguments.Length > 0 && Equals(candidate.MethodArguments[0], firstArgument))));

        string availableChildren = string.Join(", ", parent.Children.Select(candidate =>
            candidate.MemberType == EAnimationMemberType.Method && candidate.MethodArguments.Length > 0
                ? $"{candidate.MemberType}:{candidate.MemberName}({candidate.MethodArguments[0]})"
                : $"{candidate.MemberType}:{candidate.MemberName}"));
        child.ShouldNotBeNull($"Expected child '{memberType}:{memberName}' under '{parent.MemberName}'. Available children: {availableChildren}");
        return child!;
    }

    private static void SetLocalMatrix(SceneNode sceneNode, Matrix4x4 localMatrix)
    {
        Transform transform = sceneNode.GetTransformAs<Transform>(true)!;
        transform.DeriveLocalMatrix(localMatrix);
        transform.RecalculateMatrices(true, false);
        transform.SaveBindState();
    }

    private static void MatrixShouldBe(in Matrix4x4 actual, in Matrix4x4 expected, float tolerance)
    {
        actual.M11.ShouldBe(expected.M11, tolerance);
        actual.M12.ShouldBe(expected.M12, tolerance);
        actual.M13.ShouldBe(expected.M13, tolerance);
        actual.M14.ShouldBe(expected.M14, tolerance);
        actual.M21.ShouldBe(expected.M21, tolerance);
        actual.M22.ShouldBe(expected.M22, tolerance);
        actual.M23.ShouldBe(expected.M23, tolerance);
        actual.M24.ShouldBe(expected.M24, tolerance);
        actual.M31.ShouldBe(expected.M31, tolerance);
        actual.M32.ShouldBe(expected.M32, tolerance);
        actual.M33.ShouldBe(expected.M33, tolerance);
        actual.M34.ShouldBe(expected.M34, tolerance);
        actual.M41.ShouldBe(expected.M41, tolerance);
        actual.M42.ShouldBe(expected.M42, tolerance);
        actual.M43.ShouldBe(expected.M43, tolerance);
        actual.M44.ShouldBe(expected.M44, tolerance);
    }

    private sealed class TestRuntimeShaderServices : IRuntimeShaderServices
    {
        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
            => new T();

        public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => CreateShaderAsset<T>(relativePath);

        public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => Task.FromResult(CreateShaderAsset<T>(relativePath));

        public void LogWarning(string message)
        {
        }

        private static T CreateShaderAsset<T>(string relativePath) where T : XRAsset, new()
        {
            if (typeof(T) == typeof(XRShader))
            {
                TextFile source = TextFile.FromText("void main() {}\n");
                source.FilePath = relativePath;

                XRShader shader = new(EShaderType.Fragment, source)
                {
                    FilePath = relativePath,
                };

                return (T)(XRAsset)shader;
            }

            return new T();
        }
    }
}