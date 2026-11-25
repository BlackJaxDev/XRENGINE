using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Editor;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks the row layout of the stacked native inspector view of one scene node.</summary>
[TestFixture]
public sealed class InspectorStackedNodeLayoutTests
{
    private sealed class StackedLayoutProbeComponent : XRComponent
    {
        public float Strength { get; set; } = 2.0f;
    }

    private static object? Build(object?[]? objects)
        => typeof(InspectorPanel).GetMethod("BuildStackedNodeLayout", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [objects]);

    private static T Member<T>(object layout, string name)
        => (T)layout.GetType().GetProperty(name)!.GetValue(layout)!;

    [Test]
    public void OneNodeListsSummaryTransformAndComponentSections()
    {
        SceneNode node = new("Stacked");
        try
        {
            StackedLayoutProbeComponent component = node.AddComponent<StackedLayoutProbeComponent>()!;
            object layout = Build([node])!;
            Assert.That(layout, Is.Not.Null);
            var properties = Member<List<PropertyInfo>>(layout, "Properties");
            var targets = Member<Dictionary<PropertyInfo, object?[]>>(layout, "Targets");
            var headers = Member<HashSet<PropertyInfo>>(layout, "Headers");
            var names = Member<Dictionary<PropertyInfo, string>>(layout, "Names");

            Assert.That(properties[0].Name, Is.EqualTo(nameof(SceneNode.Name)));
            PropertyInfo transformHeader = properties.Single(property => property.Name == nameof(SceneNode.Transform));
            Assert.That(headers, Does.Contain(transformHeader));

            PropertyInfo componentHeader = properties.Single(property =>
                property.Name == nameof(XRComponent.IsActive) && property.ReflectedType == typeof(StackedLayoutProbeComponent));
            Assert.That(headers, Does.Contain(componentHeader));
            Assert.That(targets[componentHeader], Is.EqualTo(new object?[] { component }));
            Assert.That(names[componentHeader], Does.Contain("Probe"));

            PropertyInfo strength = properties.Single(property => property.Name == nameof(StackedLayoutProbeComponent.Strength));
            Assert.That(properties.IndexOf(strength), Is.GreaterThan(properties.IndexOf(componentHeader)));
            Assert.That(targets[strength], Is.EqualTo(new object?[] { component }));
            Assert.That(properties.Any(property => property.DeclaringType == typeof(XRComponent) &&
                !ReferenceEquals(property, componentHeader)), Is.False);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void SeveralNodesUseThePlainPropertyList()
    {
        SceneNode first = new("First"), second = new("Second");
        try { Assert.That(Build([first, second]), Is.Null); }
        finally { first.Destroy(true); second.Destroy(true); }
    }
}
