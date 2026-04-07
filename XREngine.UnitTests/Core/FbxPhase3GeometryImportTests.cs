using System.Numerics;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Fbx;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class FbxPhase3GeometryImportTests
{
    [Test]
    public void GeometryParser_ParsesSyntheticMeshPayload()
    {
        byte[] data = Encoding.UTF8.GetBytes(
            """
            ; FBX 7.4.0 project file
            Objects:  {
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
            }
            Connections:  {
                C: "OO",2000,1001
            }
            """);

        using FbxStructuralDocument structural = FbxStructuralParser.Parse(data);
        FbxSemanticDocument semantic = FbxSemanticParser.Parse(structural);
        FbxGeometryDocument geometry = FbxGeometryParser.Parse(structural, semantic);

        geometry.TryGetMeshGeometry(2000, out FbxMeshGeometry mesh).ShouldBeTrue();
        mesh.ControlPoints.Count.ShouldBe(4);
        mesh.ControlPoints[2].ShouldBe(new Vector3(1.0f, 1.0f, 0.0f));
        mesh.PolygonVertexIndices.Count.ShouldBe(4);
        mesh.PolygonVertexIndices[^1].ShouldBe(-4);
        mesh.Normals.Count.ShouldBe(1);
        mesh.Normals[0].MappingType.ShouldBe(FbxLayerElementMappingType.ByPolygonVertex);
        mesh.TextureCoordinates.Count.ShouldBe(1);
        mesh.TextureCoordinates[0].Name.ShouldBe("UVSet0");
        mesh.TextureCoordinates[0].DirectValues[3].ShouldBe(new Vector2(0.0f, 1.0f));
        mesh.Materials.ShouldNotBeNull();
        mesh.Materials!.DirectValues.Single().ShouldBe(0);
    }
}