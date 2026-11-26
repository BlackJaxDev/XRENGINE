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
        => typeof(InspectorPanel).GetMethod("BuildStackedNodeLayout", BindingFlags.NonPublic | BindingFlags.Static, [typeof(object[])])!
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
    public void SameTypeComponentsEachGetTheirOwnSection()
    {
        SceneNode node = new("Pair");
        try
        {
            StackedLayoutProbeComponent first = node.AddComponent<StackedLayoutProbeComponent>()!;
            StackedLayoutProbeComponent second = node.AddComponent<StackedLayoutProbeComponent>()!;
            object layout = Build([node])!;
            var properties = Member<List<PropertyInfo>>(layout, "Properties");
            var targets = Member<Dictionary<PropertyInfo, object?[]>>(layout, "Targets");
            PropertyInfo[] strengths = properties.Where(property => property.Name == nameof(StackedLayoutProbeComponent.Strength)).ToArray();
            Assert.That(strengths, Has.Length.EqualTo(2));
            Assert.That(strengths[0], Is.Not.SameAs(strengths[1]));
            Assert.That(targets[strengths[0]], Is.EqualTo(new object?[] { first }));
            Assert.That(targets[strengths[1]], Is.EqualTo(new object?[] { second }));
            Assert.That(strengths[1].GetValue(second), Is.EqualTo(2.0f));
            Assert.That(Build([node]) is { } rebuilt &&
                Member<List<PropertyInfo>>(rebuilt, "Properties").Contains(strengths[1]), Is.True);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void CollapsedSectionsKeepOnlyTheirHeaderRow()
    {
        SceneNode node = new("Collapsed");
        try
        {
            node.AddComponent<StackedLayoutProbeComponent>();
            MethodInfo build = typeof(InspectorPanel).GetMethod("BuildStackedNodeLayout",
                BindingFlags.NonPublic | BindingFlags.Static, [typeof(object[]), typeof(IReadOnlySet<string>)])!;
            HashSet<string> collapsed = ["Transform", typeof(StackedLayoutProbeComponent).FullName!];
            object layout = build.Invoke(null, [new object?[] { node }, collapsed])!;
            var properties = Member<List<PropertyInfo>>(layout, "Properties");
            var names = Member<Dictionary<PropertyInfo, string>>(layout, "Names");
            Assert.That(properties.Any(property => property.Name == nameof(StackedLayoutProbeComponent.Strength)), Is.False);
            Assert.That(properties.Any(property => property.ReflectedType == node.Transform.GetType() &&
                property.Name != nameof(XRComponent.IsActive)), Is.False);
            PropertyInfo header = properties.Single(property => property.Name == nameof(XRComponent.IsActive));
            Assert.That(names[header], Does.Contain("Probe"));
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void SearchKeepsMatchingMembersAndOpensCollapsedSections()
    {
        SceneNode node = new("Searched");
        try
        {
            node.AddComponent<StackedLayoutProbeComponent>();
            MethodInfo build = typeof(InspectorPanel).GetMethod("BuildStackedNodeLayout",
                BindingFlags.NonPublic | BindingFlags.Static,
                [typeof(object[]), typeof(IReadOnlySet<string>), typeof(string)])!;
            HashSet<string> collapsed = [typeof(StackedLayoutProbeComponent).FullName!];
            object layout = build.Invoke(null, [new object?[] { node }, collapsed, "streng"])!;
            var properties = Member<List<PropertyInfo>>(layout, "Properties");
            Assert.That(properties.Select(property => property.Name),
                Is.EqualTo(new[] { nameof(XRComponent.IsActive), nameof(StackedLayoutProbeComponent.Strength) }));

            object byTitle = build.Invoke(null, [new object?[] { node }, null, "probe"])!;
            Assert.That(Member<List<PropertyInfo>>(byTitle, "Properties").Any(property =>
                property.Name == nameof(StackedLayoutProbeComponent.Strength)), Is.True);

            object none = build.Invoke(null, [new object?[] { node }, null, "no such member"])!;
            Assert.That(Member<List<PropertyInfo>>(none, "Properties"), Is.Empty);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void SeveralNodesShareSectionsForComponentTypesThatEveryNodeHasOnce()
    {
        SceneNode first = new("First"), second = new("Second");
        try
        {
            StackedLayoutProbeComponent firstProbe = first.AddComponent<StackedLayoutProbeComponent>()!;
            StackedLayoutProbeComponent secondProbe = second.AddComponent<StackedLayoutProbeComponent>()!;
            object layout = Build([first, second])!;
            Assert.That(layout, Is.Not.Null);
            var properties = Member<List<PropertyInfo>>(layout, "Properties");
            var targets = Member<Dictionary<PropertyInfo, object?[]>>(layout, "Targets");

            PropertyInfo name = properties.Single(property => property.Name == nameof(SceneNode.Name));
            Assert.That(targets[name], Is.EqualTo(new object?[] { first, second }));
            PropertyInfo strength = properties.Single(property => property.Name == nameof(StackedLayoutProbeComponent.Strength));
            Assert.That(targets[strength], Is.EqualTo(new object?[] { firstProbe, secondProbe }));

            second.AddComponent<StackedLayoutProbeComponent>();
            object withDuplicate = Build([first, second])!;
            Assert.That(Member<List<PropertyInfo>>(withDuplicate, "Properties").Any(property =>
                property.Name == nameof(StackedLayoutProbeComponent.Strength)), Is.False);
        }
        finally { first.Destroy(true); second.Destroy(true); }
    }
}
