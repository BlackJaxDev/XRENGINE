using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks grouped undo for editor targets without engine change events.</summary>
[TestFixture]
[NonParallelizable]
public class EditorPropertyAccessTests
{
    [Test]
    public void PlainTargetsRestoreTheirOwnValuesInOneUndoStep()
    {
        Undo.ClearHistory();
        try
        {
            var first = new EditorPropertyTestTarget { Number = 12 };
            var second = new EditorPropertyTestTarget { Number = 25 };
            var access = new EditorPropertyAccess(
                typeof(EditorPropertyTestTarget).GetProperty(nameof(EditorPropertyTestTarget.Number))!,
                [first, second]);

            access.SetValue(37);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(first.Number, Is.EqualTo(12));
                Assert.That(second.Number, Is.EqualTo(25));
                Assert.That(Undo.CanUndo, Is.False);
            });
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(first.Number, Is.EqualTo(37));
                Assert.That(second.Number, Is.EqualTo(37));
            });
        }
        finally
        {
            Undo.ClearHistory();
        }
    }
}
