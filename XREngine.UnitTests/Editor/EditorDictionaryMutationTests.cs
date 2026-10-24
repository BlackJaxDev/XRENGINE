using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class EditorDictionaryMutationTests
{
    [SetUp]
    public void Clear() => Undo.ClearHistory();

    [TearDown]
    public void ClearAfter() => Undo.ClearHistory();

    [Test]
    public void MultiOwnerSetUsesOneUndoStep()
    {
        EditorDictionaryMutationTestTarget first = new();
        EditorDictionaryMutationTestTarget second = new();
        first.Values.Add("A", 1);
        second.Values.Add("A", 2);
        EditorDictionaryElementBindingTestDouble[] bindings =
        [
            new(Capture(first), "A"), new(Capture(second), "A")
        ];

        EditorDictionaryMutation.Set(bindings, _ => 8, null);
        Assert.That(first.Values["A"], Is.EqualTo(8));
        Assert.That(second.Values["A"], Is.EqualTo(8));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(first.Values["A"], Is.EqualTo(1));
        Assert.That(second.Values["A"], Is.EqualTo(2));
        Assert.That(Undo.CanUndo, Is.False);
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(first.Values["A"], Is.EqualTo(8));
    }

    [Test]
    public void AddAndRemoveReplayExactKeys()
    {
        EditorDictionaryMutationTestTarget target = new();
        EditorDictionaryMutation.Add([Capture(target)], null);
        string key = target.Values.Keys.Single();
        Assert.That(key, Is.EqualTo("Key"));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values, Is.Empty);
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(target.Values.Keys.Single(), Is.SameAs(key));

        EditorDictionaryMutation.Remove([Capture(target)], key, null);
        Assert.That(target.Values, Is.Empty);
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values.Keys.Single(), Is.SameAs(key));
    }

    [Test]
    public void AliasedOwnersAddOneEntry()
    {
        Dictionary<string, int> shared = new(StringComparer.Ordinal);
        EditorDictionaryMutationTestTarget first = new() { Values = shared };
        EditorDictionaryMutationTestTarget second = new() { Values = shared };
        EditorDictionaryMutation.Add([Capture(first), Capture(second)], null);
        Assert.That(shared.Count, Is.EqualTo(1));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(shared, Is.Empty);
    }

    [Test]
    public void IntDefaultComparerIsSupported()
    {
        EditorIntDictionaryMutationTestTarget target = new();
        EditorDictionaryMutation.Add([EditorDictionaryMutation.Capture(target,
            typeof(EditorIntDictionaryMutationTestTarget).GetProperty(nameof(target.Values))!,
            () => true)], null);
        Assert.That(target.Values.ContainsKey(0), Is.True);
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values, Is.Empty);
    }

    [Test]
    public void StringDefaultComparerIsSupported()
    {
        EditorDictionaryMutationTestTarget target = new()
        {
            Values = new Dictionary<string, int>()
        };
        EditorDictionaryMutation.Add([Capture(target)], null);
        Assert.That(target.Values.ContainsKey("Key"), Is.True);
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values, Is.Empty);
    }

    [Test]
    public void ForeignValueBlocksUndoAndKeepsHistory()
    {
        EditorDictionaryMutationTestTarget target = new();
        target.Values.Add("A", 1);
        EditorDictionaryMutation.Remove([Capture(target)], "A", null);
        target.Values.Add("B", 3);
        Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        Assert.That(target.Values["B"], Is.EqualTo(3));
        Assert.That(Undo.CanUndo, Is.True);
    }

    [Test]
    public void EqualForeignStringKeyDoesNotReplaceTheRecordedKey()
    {
        EditorDictionaryMutationTestTarget target = new();
        string original = new('A', 1);
        target.Values.Add(original, 1);
        EditorDictionaryElementBindingTestDouble binding = new(Capture(target), original);
        EditorDictionaryMutation.Set([binding], _ => 8, null);
        target.Values.Remove(original);
        string foreign = new('A', 1);
        target.Values.Add(foreign, 8);
        Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        Assert.That(target.Values.Keys.Single(), Is.SameAs(foreign));
        Assert.That(Undo.CanUndo, Is.True);
    }

    [Test]
    public void CustomComparerAndStaleSourceAreRejected()
    {
        EditorDictionaryMutationTestTarget target = new()
        {
            Values = new(StringComparer.OrdinalIgnoreCase)
        };
        var property = typeof(EditorDictionaryMutationTestTarget).GetProperty(nameof(target.Values))!;
        Assert.Throws<InvalidOperationException>(() => EditorDictionaryMutation.Capture(target,
            property, () => true));
        target.Values = new(StringComparer.Ordinal);
        EditorDictionarySourceLease source = Capture(target);
        target.Values = new(StringComparer.Ordinal);
        Assert.Throws<InvalidOperationException>(() => EditorDictionaryMutation.Add([source], null));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void InvalidSecondTargetLeavesFirstUntouched()
    {
        EditorDictionaryMutationTestTarget first = new();
        EditorDictionaryMutationTestTarget second = new();
        EditorDictionarySourceLease firstSource = Capture(first);
        EditorDictionarySourceLease secondSource = Capture(second);
        second.Values = new(StringComparer.Ordinal);
        Assert.Throws<InvalidOperationException>(() => EditorDictionaryMutation.Add(
            [firstSource, secondSource], null));
        Assert.That(first.Values, Is.Empty);
        Assert.That(second.Values, Is.Empty);
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void EqualForeignKeyInvalidatesVisibleRow()
    {
        EditorDictionaryMutationTestTarget target = new();
        string original = new('A', 1);
        target.Values.Add(original, 1);
        EditorDictionarySourceLease source = Capture(target);
        target.Values.Remove(original);
        target.Values.Add(new string('A', 1), 1);
        Assert.Throws<InvalidOperationException>(() => EditorDictionaryMutation.Remove(
            [source], original, null));
        Assert.That(target.Values.Count, Is.EqualTo(1));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void StaleDisplayedBindingAndThrowingNotificationKeepSafeHistory()
    {
        EditorDictionaryMutationTestTarget target = new();
        target.Values.Add("A", 1);
        EditorDictionaryElementBindingTestDouble binding = new(Capture(target), "A")
        {
            DisplayedValue = 99
        };
        Assert.Throws<InvalidOperationException>(() => EditorDictionaryMutation.Set([binding], _ => 8, null));
        Assert.That(Undo.CanUndo, Is.False);
        binding = new(Capture(target), "A")
        {
            OnAccept = () => throw new InvalidOperationException("view closed")
        };
        EditorDictionaryMutation.Set([binding], _ => 8, null);
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values["A"], Is.EqualTo(1));
    }

    private static EditorDictionarySourceLease Capture(EditorDictionaryMutationTestTarget target)
        => EditorDictionaryMutation.Capture(target,
            typeof(EditorDictionaryMutationTestTarget).GetProperty(nameof(target.Values))!,
            () => true);
}
