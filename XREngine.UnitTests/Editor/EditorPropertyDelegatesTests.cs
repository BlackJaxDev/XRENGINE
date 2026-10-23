using NUnit.Framework;
using XREngine.Editor.UI;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks cached property access without a window or graphics device.</summary>
[TestFixture]
public class EditorPropertyDelegatesTests
{
    [Test]
    public void OpenDelegatesReadAndWriteTheSuppliedInstance()
    {
        var property = typeof(Version).GetProperty(nameof(Version.Major))!;
        EditorPropertyDelegates access = EditorPropertyDelegates.Create(property);
        Assert.That(access.Get(new Version(3, 1)), Is.EqualTo(3));
        Assert.That(access.Get(new Version(7, 2)), Is.EqualTo(7));
        Assert.That(access.Set, Is.Null);
    }

    [Test]
    public void NullablePropertyPreservesNullAndTypedValues()
    {
        var property = typeof(EditorPropertyTestTarget).GetProperty(nameof(EditorPropertyTestTarget.Number))!;
        EditorPropertyDelegates access = EditorPropertyDelegates.Create(property);
        var first = new EditorPropertyTestTarget { Number = 12 };
        var second = new EditorPropertyTestTarget { Number = 25 };
        access.Set!(first, null);
        access.Set!(second, 37);
        Assert.That(access.Get(first), Is.Null);
        Assert.That(access.Get(second), Is.EqualTo(37));
    }

    [Test]
    public void IndexersAreRejectedBeforeBinding()
        => Assert.Throws<ArgumentException>(() => EditorPropertyDelegates.Create(
            typeof(string).GetProperty("Chars")!));
}
