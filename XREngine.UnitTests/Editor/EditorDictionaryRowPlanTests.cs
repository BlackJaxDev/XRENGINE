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

    [TestCase("\r")]
    [TestCase("\n")]
    [TestCase("\r\n")]
    [TestCase("\u0085")]
    [TestCase("\u2028")]
    [TestCase("\u2029")]
    public void MultilineKeyUsesNormalizedMeasuredRow(string separator)
    {
        Assert.That(EditorDictionaryRowPlan.TryCreate(
            [new([$"first{separator}second", "single"], [1, 2], typeof(string))], out var plan), Is.True);
        Assert.That(plan!.HasMultilineRows, Is.True);
        Assert.That(plan.Label(0), Is.EqualTo("first\nsecond"));
        Assert.That(plan.LineCount(0), Is.EqualTo(2));
        Assert.That(plan.LineCount(1), Is.EqualTo(1));
    }

    [Test]
    public void MultilineReadOnlyValueSetsTheRowLineCount()
    {
        Assert.That(EditorDictionaryRowPlan.TryCreate(
            [new(["common", "missing"], ["same", "a\r\nb\u2028c"], typeof(string)),
             new(["common"], ["other"], typeof(string))], out var plan), Is.True);
        Assert.That(plan!.ReadOnlyText(1), Is.EqualTo("a\nb\nc"));
        Assert.That(plan.LineCount(0), Is.EqualTo(1));
        Assert.That(plan.LineCount(1), Is.EqualTo(3));
    }

    [Test]
    public void SingleLineRowsKeepTheFixedHeightPath()
    {
        Assert.That(EditorDictionaryRowPlan.TryCreate(
            [new(["a", "b"], [1, 2], typeof(string))], out var plan), Is.True);
        Assert.That(plan!.HasMultilineRows, Is.False);
    }

    [Test]
    public void LongTextIsLimitedWithAHiddenLineCount()
    {
        string text = string.Join("\n", Enumerable.Range(0, 40));
        string display = EditorDictionaryRowPlan.ToDisplayText(text, out int lines);
        string[] shown = display.Split('\n');
        Assert.That(lines, Is.EqualTo(EditorDictionaryRowPlan.MaxDisplayLines));
        Assert.That(shown, Has.Length.EqualTo(EditorDictionaryRowPlan.MaxDisplayLines));
        Assert.That(shown[^2], Is.EqualTo("30"));
        Assert.That(shown[^1], Is.EqualTo("... (9 more lines)"));

        string exact = string.Join("\n", Enumerable.Range(0, EditorDictionaryRowPlan.MaxDisplayLines));
        Assert.That(EditorDictionaryRowPlan.ToDisplayText(exact, out lines), Is.EqualTo(exact));
        Assert.That(lines, Is.EqualTo(EditorDictionaryRowPlan.MaxDisplayLines));
    }

    [Test]
    public void RowExtentGrowsWithLineCount()
    {
        Assert.That(EditorVirtualRowExtent.ForLines(1, 14), Is.EqualTo(EditorVirtualRowExtent.SingleLineHeight));
        Assert.That(EditorVirtualRowExtent.ForLines(3, 14), Is.EqualTo(EditorVirtualRowExtent.SingleLineHeight + 37));
        Assert.That(EditorVirtualRowExtent.ForLines(3, float.NaN), Is.EqualTo(EditorVirtualRowExtent.SingleLineHeight));
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
