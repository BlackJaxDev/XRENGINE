using System;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class DocumentUniqueAnchorAssignerTests
{
    /// <summary>
    /// A converter writes a child value through a nested serialize call, and that child defines its
    /// own anchor. The outer alias written after the child must still resolve to the outer object.
    /// With the default assigner both anchors are named o0, and the outer alias resolves to the child object.
    /// </summary>
    [Test]
    public void NestedConverterAnchors_DoNotReplaceOuterAnchors()
    {
        Root root = CreateRoot();
        string yaml = CreateSerializer().Serialize(root);

        string[] anchors = Regex.Matches(yaml, @"&(\S+)").Select(match => match.Groups[1].Value).ToArray();
        anchors.Length.ShouldBe(2, yaml);
        anchors.Distinct().Count().ShouldBe(anchors.Length, yaml);

        Root read = new DeserializerBuilder().Build().Deserialize<Root>(yaml);
        read.Before.ShouldNotBeNull();
        read.After.ShouldBeSameAs(read.Before);
        read.Nested.ShouldNotBeNull();
        read.Nested.Body.ShouldNotBeNull();
        read.Nested.Body.First.ShouldNotBeNull();
        read.Nested.Body.Second.ShouldBeSameAs(read.Nested.Body.First);
    }

    [Test]
    public void AnchorNames_RestartForEachTopLevelDocument()
    {
        ISerializer serializer = CreateSerializer();
        Root root = CreateRoot();

        serializer.Serialize(root).ShouldBe(serializer.Serialize(root));
    }

    [Test]
    public void AssetSerializer_UsesDocumentScopedAnchors()
        => AssetManager.Serializer.ShouldBeOfType<DocumentScopedYamlSerializer>();

    private static ISerializer CreateSerializer()
        => new DocumentScopedYamlSerializer(
            DocumentUniqueAnchorAssigner.Register(new SerializerBuilder().WithTypeConverter(new WrapperConverter())).Build());

    private static Root CreateRoot()
    {
        Marker marker = new() { Name = "outer" };
        Leaf leaf = new() { Name = "inner" };
        return new Root
        {
            Before = marker,
            Nested = new Wrapper { Body = new Pair { First = leaf, Second = leaf } },
            After = marker,
        };
    }

    public sealed class Marker
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class Leaf
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class Pair
    {
        public Leaf? First { get; set; }
        public Leaf? Second { get; set; }
    }

    public sealed class Wrapper
    {
        public Pair? Body { get; set; }
    }

    public sealed class Root
    {
        public Marker? Before { get; set; }
        public Wrapper? Nested { get; set; }
        public Marker? After { get; set; }
    }

    /// <summary>
    /// Writes <see cref="Wrapper.Body"/> through the nested serializer, as the engine transform and
    /// asset converters do for their child values.
    /// </summary>
    private sealed class WrapperConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
            => type == typeof(Wrapper);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
            => throw new NotSupportedException("The test reads Wrapper with the default deserializer.");

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            Wrapper wrapper = (Wrapper)value!;
            emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
            emitter.Emit(new Scalar(nameof(Wrapper.Body)));
            serializer(wrapper.Body, typeof(Pair));
            emitter.Emit(new MappingEnd());
        }
    }
}
