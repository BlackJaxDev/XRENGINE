using NUnit.Framework;
using Shouldly;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class AssetsPanelSelectionTests
{
    [TestCase("unchanged")]
    [TestCase("new-selection")]
    [TestCase("destroy")]
    [TestCase("transform")]
    [TestCase("mapping")]
    public void FileSelectionPublishesOnlyForItsCurrentOwner(string change)
    {
        NativeAssetSelection.Clear();
        SceneNode node = new("Asset selection", new UIBoundableTransform());
        AssetsPanelSelectionProbe panel = node.AddComponent<AssetsPanelSelectionProbe>()!;
        node.SetWorldContext(new TestWorldContext());
        string first = Path.Combine(Path.GetTempPath(), "native-selection-first.unregistered-native-test");
        string second = Path.Combine(Path.GetTempPath(), "native-selection-second.unregistered-native-test");
        panel.AddPath(11, first);
        panel.AddPath(22, second);
        panel.AttachSelection();
        List<string> published = [];
        panel.AssetSelected += published.Add;
        bool changed = false;
        void OnSelection(object? _)
        {
            if (changed) return;
            changed = true;
            switch (change)
            {
                case "new-selection": panel.Select(22); break;
                case "destroy": panel.SetOwnerActive(false); panel.Destroy(true); break;
                case "transform": node.SetTransform<UIListTransform>(); break;
                case "mapping": panel.AddPath(11, second); break;
            }
        }
        NativeAssetSelection.SelectionChanged += OnSelection;
        try
        {
            panel.Select(11);

            if (change == "unchanged")
            {
                panel.SelectedPath.ShouldBe(first);
                NativeAssetSelection.SelectedPath.ShouldBe(first);
                published.ShouldBe([first]);
                panel.Activate(11);
                published.ShouldBe([first]);
                NativeAssetSelection.Clear();
                panel.Activate(11);
                published.ShouldBe([first, first]);
                panel.Select(null);
                panel.SelectedPath.ShouldBeNull();
                NativeAssetSelection.SelectedPath.ShouldBeNull();
            }
            else if (change == "new-selection")
            {
                panel.SelectedPath.ShouldBe(second);
                published.ShouldBe([second]);
            }
            else published.ShouldBeEmpty();
        }
        finally
        {
            NativeAssetSelection.SelectionChanged -= OnSelection;
            panel.SetOwnerActive(false);
            node.Destroy(true);
            NativeAssetSelection.Clear();
        }
    }

    [Test]
    public void FolderSelectionDoesNotNavigateUntilActivation()
    {
        NativeAssetSelection.Clear();
        SceneNode node = new("Folder selection", new UIBoundableTransform());
        AssetsPanelSelectionProbe panel = node.AddComponent<AssetsPanelSelectionProbe>()!;
        node.SetWorldContext(new TestWorldContext());
        string folder = Path.GetTempPath();
        panel.AddPath(11, folder);
        panel.AttachSelection();
        try
        {
            panel.Select(11);
            panel.SelectedPath.ShouldBe(folder);
            panel.Folder.ShouldBeEmpty();
            panel.Activate(11);
            panel.Folder.ShouldBe(folder);
        }
        finally
        {
            panel.SetOwnerActive(false);
            node.Destroy(true);
            NativeAssetSelection.Clear();
        }
    }
}
