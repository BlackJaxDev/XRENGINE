using System.IO;
using System.Numerics;
using System.Text;
using System.Globalization;
using Assimp;
using NUnit.Framework;
using Shouldly;
using XREngine.Fbx;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class FbxPhase5BinaryExportTests
{
    [Test]
    public void BinaryWriter_WritesCompressed7400Document_ThatReparses()
    {
        FbxBinaryNode root = new("Root");
        root.Properties.Add(FbxBinaryProperty.Int32(42));
        root.Properties.Add(FbxBinaryProperty.String("Hello"));
        root.Properties.Add(FbxBinaryProperty.Float32Array([1.25f, 2.5f, 5.0f]));

        byte[] data = FbxBinaryWriter.WriteToArray([root], new FbxBinaryExportOptions
        {
            BinaryVersion = 7400,
            BigEndian = false,
            ArrayEncodingMode = FbxBinaryArrayEncodingMode.ZlibCompressed,
            IncludeFooter = true,
        });

        using FbxStructuralDocument document = FbxStructuralParser.Parse(data);

        document.Header.Encoding.ShouldBe(FbxTransportEncoding.Binary);
        document.Header.BinaryVersion.ShouldBe(7400);
        document.Header.IsBigEndian.ShouldBeFalse();
        document.Footer.ShouldNotBeNull();
        document.Footer!.Value.Version.ShouldBe(7400);
        document.Nodes.Count.ShouldBe(1);

        byte[] decodedArray = FbxStructuralParser.DecodeArrayPayload(document, document.ArrayWorkItems[0]);
        BitConverter.ToSingle(decodedArray, 0).ShouldBe(1.25f);
        BitConverter.ToSingle(decodedArray, 4).ShouldBe(2.5f);
        BitConverter.ToSingle(decodedArray, 8).ShouldBe(5.0f);
    }

    [Test]
    public void BinaryWriter_Writes7500BigEndianDocument_ThatReparses()
    {
        FbxBinaryNode root = new("WideNode");
        root.Properties.Add(FbxBinaryProperty.Int32(77));

        byte[] data = FbxBinaryWriter.WriteToArray([root], new FbxBinaryExportOptions
        {
            BinaryVersion = 7500,
            BigEndian = true,
            IncludeFooter = false,
        });

        using FbxStructuralDocument document = FbxStructuralParser.Parse(data);

        document.Header.BinaryVersion.ShouldBe(7500);
        document.Header.IsBigEndian.ShouldBeTrue();
        document.Nodes.Count.ShouldBe(1);
        document.GetNodeName(document.Nodes[0]).ShouldBe("WideNode");
    }

    [Test]
    public void BinaryExporter_RoundTripsSupportedPhase4Subset()
    {
        byte[] ascii = Encoding.UTF8.GetBytes(CreateSyntheticPhase4Fbx());

        using FbxStructuralDocument structural = FbxStructuralParser.Parse(ascii);
        FbxSemanticDocument semantic = FbxSemanticParser.Parse(structural);
        FbxGeometryDocument geometry = FbxGeometryParser.Parse(structural, semantic);
        FbxDeformerDocument deformers = FbxDeformerParser.Parse(structural, semantic);
        FbxAnimationDocument animations = FbxAnimationParser.Parse(structural, semantic, deformers);

        byte[] binary = FbxBinaryExporter.Export(semantic, geometry, deformers, animations, new FbxBinaryExportOptions
        {
            BinaryVersion = 7400,
            BigEndian = false,
            ArrayEncodingMode = FbxBinaryArrayEncodingMode.Raw,
        });

        using FbxStructuralDocument reparsedStructural = FbxStructuralParser.Parse(binary);
        FbxSemanticDocument reparsedSemantic = FbxSemanticParser.Parse(reparsedStructural);
        FbxGeometryDocument reparsedGeometry = FbxGeometryParser.Parse(reparsedStructural, reparsedSemantic);
        FbxDeformerDocument reparsedDeformers = FbxDeformerParser.Parse(reparsedStructural, reparsedSemantic);
        FbxAnimationDocument reparsedAnimations = FbxAnimationParser.Parse(reparsedStructural, reparsedSemantic, reparsedDeformers);

        reparsedStructural.Header.Encoding.ShouldBe(FbxTransportEncoding.Binary);
        reparsedStructural.Header.BinaryVersion.ShouldBe(7400);

        reparsedSemantic.TryGetObject(2000, out FbxSceneObject reparsedMeshObject).ShouldBeTrue();
        string[] geometryChildNames =
        [..
            reparsedStructural.Nodes
                .Where(node => node.ParentIndex == reparsedMeshObject.NodeIndex)
                .Select(reparsedStructural.GetNodeName)];
        geometryChildNames.ShouldContain("Vertices");
        geometryChildNames.ShouldContain("PolygonVertexIndex");

        FbxNodeRecord verticesNode = reparsedStructural.Nodes.First(node => node.ParentIndex == reparsedMeshObject.NodeIndex && reparsedStructural.GetNodeName(node) == "Vertices");
        verticesNode.PropertyCount.ShouldBe(1);
        FbxPropertyRecord verticesProperty = reparsedStructural.Properties[verticesNode.FirstPropertyIndex];
        verticesProperty.Kind.ShouldBe(FbxPropertyKind.Float64Array);
        verticesProperty.ArrayLength.ShouldBe(12u);

        reparsedGeometry.TryGetMeshGeometry(2000, out FbxMeshGeometry mesh).ShouldBeTrue();
        mesh.ControlPoints.Count.ShouldBe(4);
        mesh.PolygonVertexIndices[^1].ShouldBe(-4);

        reparsedDeformers.TryGetSkinBinding(2000, out FbxSkinBinding skin).ShouldBeTrue();
        skin.Clusters.Count.ShouldBe(2);
        skin.Clusters.Sum(static cluster => cluster.ControlPointWeights.Count).ShouldBe(6);

        IReadOnlyList<FbxBlendShapeChannelBinding> blendShapes = reparsedDeformers.GetBlendShapeChannels(2000);
        blendShapes.Count.ShouldBe(1);
        blendShapes[0].Name.ShouldBe("Smile");
        blendShapes[0].PositionDeltasByControlPoint[0].ShouldBe(new Vector3(0.0f, 0.25f, 0.0f));

        reparsedAnimations.Stacks.Count.ShouldBe(1);
        reparsedAnimations.Stacks[0].Name.ShouldBe("Take 001");
        reparsedAnimations.TryGetCurve(9201, out FbxScalarCurve curve).ShouldBeTrue();
        curve.Values.Count.ShouldBe(2);
        curve.Values[1].ShouldBe(5.0f);
    }

    [Test]
    public void BinaryExporter_OutputIsReadableByAssimp()
    {
        byte[] ascii = Encoding.UTF8.GetBytes(CreateSyntheticPhase4Fbx());

        using FbxStructuralDocument structural = FbxStructuralParser.Parse(ascii);
        FbxSemanticDocument semantic = FbxSemanticParser.Parse(structural);
        FbxGeometryDocument geometry = FbxGeometryParser.Parse(structural, semantic);
        FbxDeformerDocument deformers = FbxDeformerParser.Parse(structural, semantic);
        FbxAnimationDocument animations = FbxAnimationParser.Parse(structural, semantic, deformers);

        byte[] binary = FbxBinaryExporter.Export(semantic, geometry, deformers, animations);

        string tempPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"fbx-phase5-{Guid.NewGuid():N}.fbx");
        File.WriteAllBytes(tempPath, binary);
        try
        {
            using AssimpContext context = new();
            Assimp.Scene scene;
            try
            {
                scene = context.ImportFile(tempPath, PostProcessSteps.None);
            }
            catch (AssimpException exception) when (TryExtractHexOffset(exception.Message, out int offset))
            {
                string details = DescribeBinaryOffset(binary, offset);
                throw new AssertionException($"{exception.Message}{Environment.NewLine}{details}", exception);
            }

            scene.ShouldNotBeNull();
            scene.MeshCount.ShouldBeGreaterThan(0);
            scene.RootNode.ShouldNotBeNull();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

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

    private static bool TryExtractHexOffset(string message, out int offset)
    {
        const string marker = "offset 0x";
        int markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            offset = 0;
            return false;
        }

        int startIndex = markerIndex + marker.Length;
        int endIndex = startIndex;
        while (endIndex < message.Length && Uri.IsHexDigit(message[endIndex]))
            endIndex++;

        if (endIndex == startIndex || !int.TryParse(message[startIndex..endIndex], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset))
        {
            offset = 0;
            return false;
        }

        return true;
    }

    private static string DescribeBinaryOffset(byte[] binary, int offset)
    {
        using FbxStructuralDocument document = FbxStructuralParser.Parse(binary);

        int matchingPropertyIndex = document.Properties
            .Select((candidate, index) => (candidate, index))
            .Where(entry => offset >= entry.candidate.DataOffset && offset < entry.candidate.DataOffset + entry.candidate.DataLength)
            .Select(entry => entry.index)
            .DefaultIfEmpty(-1)
            .First();

        if (matchingPropertyIndex >= 0)
        {
            FbxPropertyRecord matchingProperty = document.Properties[matchingPropertyIndex];
            FbxNodeRecord node = document.Nodes[matchingProperty.NodeIndex];
            string nodeName = document.GetNodeName(node);
            return $"FBX offset 0x{offset:X} is inside node '{nodeName}' (index {node.Index}, depth {node.Depth}) property kind {matchingProperty.Kind} at data offset 0x{matchingProperty.DataOffset:X} with length {matchingProperty.DataLength}.";
        }

        FbxNodeRecord? containingNode = document.Nodes
            .Where(candidate => candidate.NameOffset <= offset && offset < candidate.EndOffset)
            .OrderByDescending(candidate => candidate.Depth)
            .Cast<FbxNodeRecord?>()
            .FirstOrDefault();

        if (containingNode is { } nodeMatch)
            return $"FBX offset 0x{offset:X} is inside node '{document.GetNodeName(nodeMatch)}' (index {nodeMatch.Index}, depth {nodeMatch.Depth}) but not inside a property payload.";

        return $"FBX offset 0x{offset:X} was not matched to any parsed property payload or node range.";
    }
}