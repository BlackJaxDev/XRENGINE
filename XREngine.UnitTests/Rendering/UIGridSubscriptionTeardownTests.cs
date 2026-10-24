using System.Reflection;
using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class UIGridSubscriptionTeardownTests
{
    [Test]
    public void ReplacementAndDirectDestroyDetachExactListsDefinitionsAndValues()
    {
        SceneNode node = new("Grid") { IsActiveSelf = false };
        UIGridTransform grid = node.SetTransform<UIGridTransform>();
        EventList<UISizingDefinition> oldRows = grid.Rows;
        UISizingValue sharedValue = new() { Value = 12 };
        UISizingDefinition definition = new() { Value = sharedValue };
        oldRows.Add(definition);
        EventList<UISizingDefinition> replacement = [definition];
        try
        {
            grid.Rows = replacement;
            Assert.That(HasHandler(oldRows, "CollectionChanged", grid), Is.False);
            Assert.That(HasHandler(replacement, "CollectionChanged", grid), Is.True);
            grid.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(HasHandler(replacement, "CollectionChanged", grid), Is.False);
                Assert.That(HasHandler(grid.Columns, "CollectionChanged", grid), Is.False);
                Assert.That(HasHandler(definition, "PropertyChanged", grid), Is.False);
                Assert.That(HasHandler(sharedValue, "PropertyChanged", grid), Is.False);
            });
            Assert.DoesNotThrow(() => { oldRows.Clear(); replacement.Clear(); sharedValue.Value = 15; });
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void DestroyDuringRowsReplacementCannotAttachTheNewList()
    {
        SceneNode node = new("Grid") { IsActiveSelf = false };
        UIGridTransform grid = node.SetTransform<UIGridTransform>();
        EventList<UISizingDefinition> previous = grid.Rows;
        EventList<UISizingDefinition> replacement = [];
        grid.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(UIGridTransform.Rows))
                grid.Destroy(true);
        };
        try
        {
            grid.Rows = replacement;
            Assert.Multiple(() =>
            {
                Assert.That(HasHandler(previous, "CollectionChanged", grid), Is.False);
                Assert.That(HasHandler(replacement, "CollectionChanged", grid), Is.False);
            });
        }
        finally { node.Destroy(true); }
    }

    private static bool HasHandler(object source, string name, object owner)
    {
        for (Type? type = source.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is not { } field)
                continue;
            return (field.GetValue(source) as Delegate)?.GetInvocationList()
                .Any(item => ReferenceEquals(item.Target, owner)) == true;
        }
        Assert.Fail($"Missing event field {name} on {source.GetType().Name}");
        return false;
    }
}
