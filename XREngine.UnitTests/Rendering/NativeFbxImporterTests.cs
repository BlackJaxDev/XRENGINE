using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
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
using XREngine.Scene;
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