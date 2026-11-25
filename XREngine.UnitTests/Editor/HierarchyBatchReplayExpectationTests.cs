using System.Numerics;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class HierarchyBatchReplayExpectationTests
{
    [Test]
    public void UndoAndRedoAdvanceOnlyAdmittedKeysAcrossActualTransforms()
    {
        Transform first = new() { Translation = new Vector3(1, 0, 0) };
        Transform second = new() { Scale = new Vector3(2) };
        try
        {
            HierarchyBatchReplayExpectation expected = new([first, second]);
            first.Translation = new Vector3(10, 0, 0);
            expected.CaptureApplied(0);
            second.Scale = new Vector3(20);
            expected.CaptureApplied(1);

            expected.BeginReplay(undo: true);
            expected.BeginProperty(first, nameof(Transform.Translation));
            first.Translation = new Vector3(1, 0, 0);
            expected.RequireCurrent();
            expected.EndProperty();
            Assert.Throws<InvalidOperationException>(expected.RequireComplete);
            expected.BeginProperty(second, nameof(Transform.Scale));
            second.Scale = new Vector3(2);
            expected.RequireCurrent();
            expected.EndProperty();
            expected.RequireComplete();
            expected.EndReplay();

            expected.BeginReplay(undo: false);
            expected.BeginProperty(first, nameof(Transform.Translation));
            first.Translation = new Vector3(10, 0, 0);
            expected.RequireCurrent();
            expected.EndProperty();
            expected.BeginProperty(second, nameof(Transform.Scale));
            second.Scale = new Vector3(20);
            expected.RequireCurrent();
            expected.EndProperty();
            expected.RequireComplete();
            expected.EndReplay();
        }
        finally { first.Destroy(true); second.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReplayRejectsPeerChangeAndSetterReentry(bool changePeer)
    {
        Transform first = new() { Translation = new Vector3(1, 0, 0) };
        Transform second = new() { Translation = new Vector3(2, 0, 0) };
        try
        {
            HierarchyBatchReplayExpectation expected = new([first, second]);
            first.Translation = new Vector3(10, 0, 0);
            expected.CaptureApplied(0);
            second.Translation = new Vector3(20, 0, 0);
            expected.CaptureApplied(1);
            expected.BeginReplay(undo: true);
            expected.BeginProperty(first, nameof(Transform.Translation));
            first.Translation = new Vector3(1, 0, 0);
            if (changePeer)
            {
                second.Translation = new Vector3(25, 0, 0);
                Assert.Throws<InvalidOperationException>(expected.RequireCurrent);
            }
            else
                Assert.Throws<InvalidOperationException>(() =>
                    expected.BeginProperty(second, nameof(Transform.Translation)));
            expected.EndProperty();
        }
        finally { first.Destroy(true); second.Destroy(true); }
    }

    [Test]
    public void MatrixBackedTransformChangesOnlyAtStructuralInverseBoundaries()
    {
        HierarchyBatchMatrixTransformProbe transform = new(Matrix4x4.CreateTranslation(1, 0, 0));
        try
        {
            HierarchyBatchReplayExpectation expected = new([transform]);
            transform.SetMatrix(Matrix4x4.CreateTranslation(10, 0, 0));
            expected.CaptureApplied(0);
            expected.BeginReplay(undo: true);
            expected.RequireComplete();
            transform.SetMatrix(Matrix4x4.CreateTranslation(1, 0, 0));
            expected.RestoreBefore(0);
            expected.EndReplay();
            expected.BeginReplay(undo: false);
            expected.RequireComplete();
            transform.SetMatrix(Matrix4x4.CreateTranslation(10, 0, 0));
            expected.RestoreAfter(0);
            expected.EndReplay();
        }
        finally { transform.Destroy(true); }
    }

}
