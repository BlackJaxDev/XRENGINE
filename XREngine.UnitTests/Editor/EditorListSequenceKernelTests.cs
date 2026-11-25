using System.Collections;
using System.Numerics;
using NUnit.Framework;
using XREngine.Editor.UI;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class EditorListSequenceKernelTests
{
    [Test]
    public void ListInsertAndInverseKeepEveryElement()
    {
        List<int> list = [1, 2, 3];
        object?[] before = EditorListSequenceKernel.Snapshot(list);
        object?[] after = EditorListSequenceKernel.Stage(before, EEditorListMutation.Insert, 1, 8);
        EditorListSequenceKernel.Mutate(list, EEditorListMutation.Insert, 1, before, after, true);
        Assert.That(list, Is.EqualTo(new[] { 1, 8, 2, 3 }));
        EditorListSequenceKernel.Mutate(list, EEditorListMutation.Insert, 1, before, after, false);
        Assert.That(list, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void ArraySetAndArrayListRemoveRestoreExactContents()
    {
        int[] array = [4, 5];
        object?[] before = EditorListSequenceKernel.Snapshot(array);
        object?[] after = EditorListSequenceKernel.Stage(before, EEditorListMutation.Set, 0, 9);
        EditorListSequenceKernel.Mutate(array, EEditorListMutation.Set, 0, before, after, true);
        EditorListSequenceKernel.Mutate(array, EEditorListMutation.Set, 0, before, after, false);
        Assert.That(array, Is.EqualTo(new[] { 4, 5 }));

        ArrayList loose = new() { "a", "b", "c" };
        before = EditorListSequenceKernel.Snapshot(loose);
        after = EditorListSequenceKernel.Stage(before, EEditorListMutation.Remove, 1, null);
        EditorListSequenceKernel.Mutate(loose, EEditorListMutation.Remove, 1, before, after, true);
        EditorListSequenceKernel.Mutate(loose, EEditorListMutation.Remove, 1, before, after, false);
        Assert.That(loose.Cast<object?>(), Is.EqualTo(new object?[] { "a", "b", "c" }));
    }

    [Test]
    public void IntrinsicVectorValuesMatchWithoutReferenceIdentity()
    {
        List<Vector3> list = [new(1, 2, 3)];
        object?[] before = EditorListSequenceKernel.Snapshot(list);
        Assert.That(EditorListSequenceKernel.Matches(list, before), Is.True);
        list[0] = new Vector3(1, 2, 4);
        Assert.That(EditorListSequenceKernel.Matches(list, before), Is.False);
    }
}
