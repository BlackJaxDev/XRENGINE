using System.Reflection;
using NUnit.Framework;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public class NativeAssetSelectionDispatchTests
{
    [Test]
    public void CompletedLoadOnRenderOwnerPublishesOnceAndRejectsStaleRevision()
    {
        const string path = "/native-selection-dispatch.asset";
        int previousRenderThread = RuntimeEngine.RenderThreadId;
        int notifications = 0;
        void SelectionChanged(object? _) => notifications++;
        NativeAssetSelection.SelectionChanged += SelectionChanged;
        try
        {
            NativeAssetSelection.Clear();
            int revision = (int)typeof(NativeAssetSelection)
                .GetField("_revision", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            typeof(NativeAssetSelection).GetField("<SelectedPath>k__BackingField",
                BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, path);
            notifications = 0;
            RuntimeEngine.AssignRenderThread(Environment.CurrentManagedThreadId);

            NativeAssetSelection.DispatchLoadedAsset(path, revision, null, "completed failure");
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(NativeAssetSelection.SelectedTarget,
                Is.EqualTo(new NativeAssetSelectionStatus(path, "completed failure")));

            NativeAssetSelection.DispatchLoadedAsset(path, revision - 1, null, "stale");
            Assert.That(notifications, Is.EqualTo(1));
        }
        finally
        {
            RuntimeEngine.AssignRenderThread(previousRenderThread);
            NativeAssetSelection.Clear();
            NativeAssetSelection.SelectionChanged -= SelectionChanged;
        }
    }
}
