using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;
using XREngine.Animation;
using XREngine.Core.Files;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class CookedBinarySchemaInspectionTests
{
    [Test]
    public void InspectSchema_Type_ReturnsReflectionMemberLayout()
    {
        CookedBinarySchema schema = CookedBinarySerializer.InspectSchema(typeof(InspectablePayload));
        string text = schema.ToAsciiTree();

        schema.Root.Marker.ShouldBe("Object");
        text.ShouldContain("Count");
        text.ShouldContain("Label");
        text.ShouldContain("memberCount");
        text.ShouldContain("runtime may use MemoryPack when supported");
    }

    [Test]
    public void InspectValue_Object_ReportMatchesCalculatedSizeAndShowsValues()
    {
        var value = new InspectablePayload
        {
            Label = "alpha",
            Count = 7,
            Tags = ["one", "two"]
        };

        CookedBinarySchema schema = CookedBinarySerializer.InspectValue(value);
        string text = schema.ToAsciiTree();

        schema.TotalSize.ShouldBe(CookedBinarySerializer.CalculateSize(value));
        text.ShouldContain("alpha");
        text.ShouldContain("7");
        text.ShouldContain("Tags");
        text.ShouldContain("List");
    }

    [Test]
    public void InspectValue_Primitive_ReportMatchesSerializedLength()
    {
        const string value = "schema-check";

        CookedBinarySchema schema = CookedBinarySerializer.InspectValue(value);
        byte[] bytes = CookedBinarySerializer.Serialize(value);
        string text = schema.ToAsciiTree();

        schema.TotalSize.ShouldBe(bytes.Length);
        schema.Root.Marker.ShouldBe("String");
        text.ShouldContain("utf8ByteCount");
        text.ShouldContain("schema-check");
    }

    [Test]
    public void InspectValue_AnimationClip_ExpandsCustomPayloadModel()
    {
        AnimationClip clip = new()
        {
            Name = "ClipA",
            LengthInSeconds = 1.25f,
            Looped = true,
            SampleRate = 60
        };

        CookedBinarySchema schema = CookedBinarySerializer.InspectValue(clip);
        string text = schema.ToAsciiTree();

        schema.TotalSize.ShouldBe(CookedBinarySerializer.CalculateSize(clip));
        text.ShouldContain("AnimationClipSerializedModel");
        text.ShouldContain("LengthInSeconds");
        text.ShouldContain("SampleRate");
        text.ShouldNotContain("opaque ICookedBinarySerializable payload");
    }

    [Test]
    public void InspectSchema_BlendTree_ExpandsSerializedModelShape()
    {
        CookedBinarySchema schema = CookedBinarySerializer.InspectSchema(typeof(BlendTree1D));
        string text = schema.ToAsciiTree();

        text.ShouldContain("BlendTree1DSerializedModel");
        text.ShouldContain("ParameterName");
        text.ShouldContain("Children");
        text.ShouldContain("serialized blend tree model");
    }

    [Test]
    public void InspectValue_BlendTree_ExpandsCustomPayloadModel()
    {
        BlendTree1D blendTree = new()
        {
            Name = "Locomotion",
            ParameterName = "Speed",
            Children =
            [
                new BlendTree1D.Child
                {
                    Threshold = 0.5f,
                    Speed = 1.0f,
                    HumanoidMirror = false
                }
            ]
        };

        CookedBinarySchema schema = CookedBinarySerializer.InspectValue(blendTree);
        string text = schema.ToAsciiTree();

        schema.TotalSize.ShouldBe(CookedBinarySerializer.CalculateSize(blendTree));
        text.ShouldContain("BlendTree1DSerializedModel");
        text.ShouldContain("Threshold");
        text.ShouldContain("Speed");
        text.ShouldNotContain("customPayload");
    }

    private sealed class InspectablePayload
    {
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<string> Tags { get; set; } = [];
    }
}