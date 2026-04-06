using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Assimp;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

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
                    FbxBackend = FbxImportBackend.Native,
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
                C: "OO",1001,1000
                C: "OO",2000,1001
                C: "OO",3000,1001
                C: "OP",4000,3000,"DiffuseColor"
                C: "OO",5000,4000
            }
            """.Replace("__TEXTURE__", relativeTexturePath, StringComparison.Ordinal);

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