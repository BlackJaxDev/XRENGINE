using NUnit.Framework;
using XREngine.Editor.UI;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class EditorDictionaryRowPlanTests
{
    [Test]
    public void PrimaryOrderAndMissingMembershipRemainExactAcrossAllOwners()
    {
        EditorDictionarySnapshot first = new(["z", "a", "Z"], [3, 5, 7], typeof(string));
        EditorDictionarySnapshot second = new(["extra", "a", "z", "Z"], [11, 13, 17, 19], typeof(string));
        EditorDictionarySnapshot third = new(["z", "a"], [23, 29], typeof(string));
        Assert.That(EditorDictionaryRowPlan.TryCreate([first, second, third], out var plan), Is.True);
        Assert.That(plan!.Count, Is.EqualTo(3));
        Assert.That(Enumerable.Range(0, plan.Count).Select(plan.Label), Is.EqualTo(new[] { "z", "a", "Z" }));
        Assert.That(plan.ReadOnlyText(0), Is.Null);
        Assert.That(plan.ReadOnlyText(1), Is.Null);
        Assert.That(plan.ReadOnlyText(2), Is.EqualTo("7"));
        Assert.That(third.IndexOf("Z"), Is.EqualTo(-1));
        Assert.That(first.Values, Is.EqualTo(new object[] { 3, 5, 7 }));
    }

    [Test]
    public void EmptyAndNullReadOnlyValuesDoNotBecomeEditable()
    {
        EditorDictionarySnapshot first = new([8, -2], [string.Empty, null], typeof(int));
        EditorDictionarySnapshot second = new([], [], typeof(int));
        Assert.That(EditorDictionaryRowPlan.TryCreate([first, second], out var plan), Is.True);
        Assert.That(plan!.Label(0), Is.EqualTo("8"));
        Assert.That(plan.Label(1), Is.EqualTo("-2"));
        Assert.That(plan.ReadOnlyText(0), Is.EqualTo(string.Empty));
        Assert.That(plan.ReadOnlyText(1), Is.EqualTo("<null>"));
    }

    [Test]
    public void EmptyPrimaryDoesNotDisplaySecondaryKeys()
    {
        Assert.That(EditorDictionaryRowPlan.TryCreate(
            [new([], [], typeof(string)), new(["other"], [1], typeof(string))], out var plan), Is.True);
        Assert.That(plan!.Count, Is.Zero);
    }

    [TestCase('\r')]
    [TestCase('\n')]
    [TestCase('\u0085')]
    [TestCase('\u2028')]
    [TestCase('\u2029')]
    public void MultilineKeyRejectsFixedHeightPlan(char separator)
    {
        Assert.That(EditorDictionaryRowPlan.TryCreate(
            [new([$"first{separator}second"], [1], typeof(string))], out var plan), Is.False);
        Assert.That(plan, Is.Null);
    }

    [TestCase('\r')]
    [TestCase('\n')]
    [TestCase('\u0085')]
    [TestCase('\u2028')]
    [TestCase('\u2029')]
    public void MultilineReadOnlyValueRejectsWholePlan(char separator)
    {
        Assert.That(EditorDictionaryRowPlan.TryCreate(
            [new(["common", "missing"], ["same", $"first{separator}second"], typeof(string)),
             new(["common"], ["other"], typeof(string))], out var plan), Is.False);
        Assert.That(plan, Is.Null);
    }

    [Test]
    public void CommonValuesKeepExistingEditableAdmission()
    {
        Assert.That(EditorDictionaryRowPlan.TryCreate(
            [new(["common"], ["first\nsecond"], typeof(string)),
             new(["common"], ["other"], typeof(string))], out var plan), Is.True);
        Assert.That(plan!.ReadOnlyText(0), Is.Null);
    }
}
