using System.Numerics;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Fbx;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class FbxPhase2SemanticGraphTests
{
    [Test]
    public void SemanticParser_ParsesSyntheticOracleFixture()
    {
        byte[] data = Encoding.UTF8.GetBytes(
            """
            ; FBX 7.4.0 project file
            GlobalSettings:  {
                Properties70:  {
                    P: "UpAxis", "int", "Integer", "",1
                    P: "UpAxisSign", "int", "Integer", "",1
                    P: "FrontAxis", "int", "Integer", "",2
                    P: "FrontAxisSign", "int", "Integer", "",1
                    P: "CoordAxis", "int", "Integer", "",0
                    P: "CoordAxisSign", "int", "Integer", "",1
                    P: "OriginalUpAxis", "int", "Integer", "",1
                    P: "OriginalUpAxisSign", "int", "Integer", "",1
                    P: "UnitScaleFactor", "double", "Number", "",1
                    P: "OriginalUnitScaleFactor", "double", "Number", "",1
                    P: "DefaultCamera", "KString", "", "", "Producer Perspective"
                }
            }
            Definitions:  {
                Version: 100
                Count: 7
                ObjectType: "Model" {
                    Count: 2
                    PropertyTemplate: "FbxNode" {
                        Properties70:  {
                            P: "RotationOrder", "enum", "", "",0
                            P: "RotationPivot", "Vector3D", "Vector", "",0,0,0
                        }
                    }
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
                ObjectType: "Deformer" {
                    Count: 2
                }
                ObjectType: "AnimationStack" {
                    Count: 1
                }
            }
            Objects:  {
                Model: 1000, "Model::Root", "Null" {
                    Version: 232
                    Properties70:  {
                        P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0
                        P: "Lcl Rotation", "Lcl Rotation", "", "A",0,90,0
                        P: "Lcl Scaling", "Lcl Scaling", "", "A",1,1,1
                    }
                    Shading: T
                }
                Model: 1001, "Model::MeshNode", "Mesh" {
                    Version: 232
                    Properties70:  {
                        P: "InheritType", "enum", "", "",1
                        P: "RotationOffset", "Vector3D", "Vector", "",1,0,0
                        P: "RotationPivot", "Vector3D", "Vector", "",1,0,0
                        P: "PreRotation", "Vector3D", "Vector", "",0,10,0
                        P: "PostRotation", "Vector3D", "Vector", "",0,20,0
                        P: "Lcl Translation", "Lcl Translation", "", "A",10,20,30
                        P: "Lcl Rotation", "Lcl Rotation", "", "A",0,90,0
                        P: "Lcl Scaling", "Lcl Scaling", "", "A",2,3,4
                        P: "GeometricTranslation", "Vector3D", "Vector", "",5,0,0
                    }
                    Culling: "CullingOff"
                }
                Geometry: 2000, "Geometry::MeshShape", "Mesh" {
                }
                Material: 3000, "Material::Stone", "" {
                }
                Texture: 4000, "Texture::StoneColor", "" {
                }
                Video: 5000, "Video::StoneColorPng", "Clip" {
                }
                Deformer: 6000, "Deformer::Skin", "Skin" {
                }
                Deformer: 6001, "SubDeformer::Cluster0", "Cluster" {
                }
                AnimationStack: 7000, "AnimStack::Take001", "" {
                }
            }
            Connections:  {
                C: "OO",1001,1000
                C: "OO",2000,1001
                C: "OO",3000,1001
                C: "OO",4000,3000
                C: "OO",5000,4000
                C: "OO",6000,2000
                C: "OO",6001,6000
                Connect: "OO", "AnimStack::Take001", "Model::MeshNode"
            }
            Takes:  {
                Current: "Take 001"
                Take: "Take 001" {
                    FileName: "synthetic.tak"
                    LocalTime: 0,1924423250
                    ReferenceTime: 0,1924423250
                }
            }
            """);

        FbxSemanticDocument document = FbxSemanticParser.Parse(data);

        document.Header.Encoding.ShouldBe(FbxTransportEncoding.Ascii);
        document.GlobalSettings.ShouldNotBeNull();
        document.GlobalSettings!.AxisSystem.UpAxis.AxisIndex.ShouldBe(1);
        document.GlobalSettings.AxisSystem.FrontAxis.AxisIndex.ShouldBe(2);
        document.GlobalSettings.AxisSystem.CoordAxis.AxisIndex.ShouldBe(0);
        document.GlobalSettings.DefaultCamera.ShouldBe("Producer Perspective");

        document.Definitions.Count.ShouldBe(7);
        document.Definitions.First(x => x.TypeName == "Model").Count.ShouldBe(2);
        document.Definitions.First(x => x.TypeName == "Model").Templates[0].Properties.ShouldContainKey("RotationOrder");

        document.Objects.Count.ShouldBe(9);
        document.Connections.Count.ShouldBe(8);
        document.Takes.Count.ShouldBe(1);
        document.Takes[0].Name.ShouldBe("Take 001");
        document.Takes[0].FileName.ShouldBe("synthetic.tak");
        document.Takes[0].LocalTime.ShouldBe("0,1924423250");

        document.TryGetObject(1001, out FbxSceneObject meshNodeObject).ShouldBeTrue();
        meshNodeObject.DisplayName.ShouldBe("MeshNode");
        meshNodeObject.Subclass.ShouldBe("Mesh");
        meshNodeObject.InlineAttributes["Culling"].AsString().ShouldBe("CullingOff");
        meshNodeObject.TransformSemantics.LocalTranslation.ShouldBe(new Vector3(10.0f, 20.0f, 30.0f));
        meshNodeObject.TransformSemantics.LocalScaling.ShouldBe(new Vector3(2.0f, 3.0f, 4.0f));
        meshNodeObject.TransformSemantics.GeometricTranslation.ShouldBe(new Vector3(5.0f, 0.0f, 0.0f));
        meshNodeObject.TransformSemantics.HasPivotData.ShouldBeTrue();
        meshNodeObject.TransformSemantics.CreateGeometryTransform().Translation.ShouldBe(new Vector3(5.0f, 0.0f, 0.0f));

        Matrix4x4 bakedLocal = meshNodeObject.TransformSemantics.CreateNodeLocalTransform(FbxPivotImportPolicy.BakeIntoLocalTransform);
        bakedLocal.Translation.ShouldBe(new Vector3(10.0f, 20.0f, 30.0f));
        Matrix4x4 preservedLocal = meshNodeObject.TransformSemantics.CreateNodeLocalTransform(FbxPivotImportPolicy.PreservePivotSemantics);
        preservedLocal.ShouldNotBe(bakedLocal);

        document.IntermediateScene.Nodes.Count.ShouldBe(2);
        document.IntermediateScene.Nodes[0].Name.ShouldBe("Root");
        document.IntermediateScene.Nodes[0].ParentNodeIndex.ShouldBeNull();
        document.IntermediateScene.Nodes[0].ChildNodeIndices.ShouldContain(1);
        document.IntermediateScene.Nodes[1].Name.ShouldBe("MeshNode");
        document.IntermediateScene.Nodes[1].ParentNodeIndex.ShouldBe(0);
        document.IntermediateScene.Meshes.Count.ShouldBe(1);
        document.IntermediateScene.Meshes[0].ModelObjectIds.ShouldContain(1001L);
        document.IntermediateScene.Materials.Count.ShouldBe(1);
        document.IntermediateScene.Materials[0].ModelObjectIds.ShouldContain(1001L);
        document.IntermediateScene.Textures.Count.ShouldBe(1);
        document.IntermediateScene.Textures[0].MaterialObjectIds.ShouldContain(3000L);
        document.IntermediateScene.Textures[0].VideoObjectIds.ShouldContain(5000L);
        document.IntermediateScene.Skins.Count.ShouldBe(1);
        document.IntermediateScene.Clusters.Count.ShouldBe(1);
        document.IntermediateScene.AnimationStacks.Count.ShouldBe(1);
    }

    [Test]
    public void SemanticParser_Parses_CheckedInSponzaAsciiSemantics()
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        string yUpPath = Path.Combine(workspaceRoot, "Build", "CommonAssets", "Models", "main1_sponza", "NewSponza_Main_Yup_003.fbx");
        string zUpPath = Path.Combine(workspaceRoot, "Build", "CommonAssets", "Models", "main1_sponza", "NewSponza_Main_Zup_003.fbx");

        FbxSemanticDocument yUp = FbxSemanticParser.ParseFile(yUpPath);
        FbxSemanticDocument zUp = FbxSemanticParser.ParseFile(zUpPath);

        yUp.Header.Encoding.ShouldBe(FbxTransportEncoding.Ascii);
        zUp.Header.Encoding.ShouldBe(FbxTransportEncoding.Ascii);

        yUp.GlobalSettings.ShouldNotBeNull();
        zUp.GlobalSettings.ShouldNotBeNull();
        yUp.GlobalSettings!.AxisSystem.UpAxis.AxisIndex.ShouldBe(1);
        zUp.GlobalSettings!.AxisSystem.UpAxis.AxisIndex.ShouldBe(2);
        yUp.GlobalSettings.AxisSystem.FrontAxis.AxisIndex.ShouldBe(2);
        zUp.GlobalSettings.AxisSystem.FrontAxis.AxisIndex.ShouldBe(1);
        zUp.GlobalSettings.AxisSystem.FrontAxis.Sign.ShouldBe(-1);
        yUp.GlobalSettings.AxisSystem.UnitScaleFactor.ShouldBe(1.0d);
        zUp.GlobalSettings.AxisSystem.UnitScaleFactor.ShouldBe(1.0d);

        yUp.Definitions.First(x => x.TypeName == "Model").Count.ShouldBe(yUp.IntermediateScene.Nodes.Count);
        zUp.Definitions.First(x => x.TypeName == "Model").Count.ShouldBe(zUp.IntermediateScene.Nodes.Count);
        yUp.Definitions.First(x => x.TypeName == "Material").Count.ShouldBe(yUp.IntermediateScene.Materials.Count);
        zUp.Definitions.First(x => x.TypeName == "Material").Count.ShouldBe(zUp.IntermediateScene.Materials.Count);
        yUp.Definitions.First(x => x.TypeName == "Texture").Count.ShouldBe(yUp.IntermediateScene.Textures.Count);
        zUp.Definitions.First(x => x.TypeName == "Texture").Count.ShouldBe(zUp.IntermediateScene.Textures.Count);

        yUp.IntermediateScene.Meshes.Count.ShouldBeGreaterThan(0);
        zUp.IntermediateScene.Meshes.Count.ShouldBe(yUp.IntermediateScene.Meshes.Count);
        yUp.Connections.Count.ShouldBeGreaterThan(0);
        zUp.Connections.Count.ShouldBe(yUp.Connections.Count);
        yUp.IntermediateScene.Nodes.Any(static node => node.ParentNodeIndex is null).ShouldBeTrue();
        zUp.IntermediateScene.Nodes.Any(static node => node.ParentNodeIndex is null).ShouldBeTrue();

        yUp.IntermediateScene.Nodes.Any(node => node.Subclass == "Mesh").ShouldBeTrue();
        zUp.IntermediateScene.Nodes.Any(node => node.Subclass == "Mesh").ShouldBeTrue();
    }

    [Test]
    public void SemanticParser_ExcludesShapeGeometryFromIntermediateMeshes()
    {
        byte[] data = Encoding.UTF8.GetBytes(
            """
            ; FBX 7.4.0 project file
            Objects:  {
                Model: 1000, "Model::Root", "Null" {
                }
                Model: 1001, "Model::MeshNode", "Mesh" {
                }
                Geometry: 2000, "Geometry::BaseMesh", "Mesh" {
                }
                Geometry: 2001, "Geometry::SmileShape", "Shape" {
                }
                Deformer: 3000, "Deformer::BlendShape", "BlendShape" {
                }
                Deformer: 3001, "Deformer::Smile", "BlendShapeChannel" {
                }
            }
            Connections:  {
                C: "OO",1001,1000
                C: "OO",2000,1001
                C: "OO",3000,2000
                C: "OO",3001,3000
                C: "OO",2001,3001
            }
            """);

        FbxSemanticDocument document = FbxSemanticParser.Parse(data);

        document.IntermediateScene.Meshes.Count.ShouldBe(1);
        document.IntermediateScene.Meshes[0].ObjectId.ShouldBe(2000L);
        document.IntermediateScene.Meshes[0].GeometryType.ShouldBe("Mesh");
        document.IntermediateScene.BlendShapes.Count.ShouldBe(2);
        document.TryGetObject(2001, out FbxSceneObject shapeGeometry).ShouldBeTrue();
        shapeGeometry.Subclass.ShouldBe("Shape");
    }

    [Test]
    public void SemanticParser_NormalizesBinaryObjectNameSuffixes()
    {
        FbxBinaryNode objects = new("Objects");
        objects.Children.Add(CreateBinaryObjectNode("Model", 1000, $"Root\0\u0001Model", "Null"));
        objects.Children.Add(CreateBinaryObjectNode("Model", 1001, $"Meshes\0\u0001Model", "Mesh"));
        objects.Children.Add(CreateBinaryObjectNode("Geometry", 2000, $"BaseMesh\0\u0001Geometry", "Mesh"));
        objects.Children.Add(CreateBinaryObjectNode("Geometry", 2001, $"SmileShape\0\u0001Geometry", "Shape"));
        objects.Children.Add(CreateBinaryObjectNode("Deformer", 3000, $"BlendShape\0\u0001Deformer", "BlendShape"));
        objects.Children.Add(CreateBinaryObjectNode("Deformer", 3001, $"Smile\0\u0001SubDeformer", "BlendShapeChannel"));

        FbxBinaryNode connections = new("Connections");
        connections.Children.Add(CreateBinaryConnectionNode(1001, 1000));
        connections.Children.Add(CreateBinaryConnectionNode(2000, 1001));
        connections.Children.Add(CreateBinaryConnectionNode(3000, 2000));
        connections.Children.Add(CreateBinaryConnectionNode(3001, 3000));
        connections.Children.Add(CreateBinaryConnectionNode(2001, 3001));

        byte[] data = FbxBinaryWriter.WriteToArray([objects, connections]);

        FbxSemanticDocument document = FbxSemanticParser.Parse(data);

        document.TryGetObject(1001, out FbxSceneObject meshNodeObject).ShouldBeTrue();
        meshNodeObject.QualifiedName.ShouldBe("Model::Meshes");
        meshNodeObject.DisplayName.ShouldBe("Meshes");

        document.TryGetObject(3001, out FbxSceneObject blendShapeChannelObject).ShouldBeTrue();
        blendShapeChannelObject.QualifiedName.ShouldBe("SubDeformer::Smile");
        blendShapeChannelObject.DisplayName.ShouldBe("Smile");

        document.IntermediateScene.Nodes.Count.ShouldBe(2);
        document.IntermediateScene.Nodes[1].Name.ShouldBe("Meshes");
        document.IntermediateScene.BlendShapes.Single(entry => entry.ObjectId == 3001L).Name.ShouldBe("Smile");
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the workspace root for the FBX phase 2 tests.");
    }

    private static FbxBinaryNode CreateBinaryObjectNode(string className, long objectId, string qualifiedName, string subtype)
    {
        FbxBinaryNode node = new(className);
        node.Properties.Add(FbxBinaryProperty.Int64(objectId));
        node.Properties.Add(FbxBinaryProperty.String(qualifiedName));
        node.Properties.Add(FbxBinaryProperty.String(subtype));
        return node;
    }

    private static FbxBinaryNode CreateBinaryConnectionNode(long sourceObjectId, long destinationObjectId)
    {
        FbxBinaryNode node = new("C");
        node.Properties.Add(FbxBinaryProperty.String("OO"));
        node.Properties.Add(FbxBinaryProperty.Int64(sourceObjectId));
        node.Properties.Add(FbxBinaryProperty.Int64(destinationObjectId));
        return node;
    }
}