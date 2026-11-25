using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static partial class UserInterface
    {
        private static void CreateVirtualCollectionFixture(SceneNode canvasRoot, int itemCount)
        {
            if (itemCount is not (100_000 or 1_000_000))
                throw new ArgumentOutOfRangeException(nameof(itemCount), "Choose 100000 or 1000000 logical items.");

            SceneNode fixtureNode = new(canvasRoot) { Name = "Virtual Collection Fixture" };
            UIListTransform list = fixtureNode.SetTransform<UIListTransform>();
            list.MinAnchor = new Vector2(0, 1);
            list.MaxAnchor = new Vector2(0, 1);
            list.NormalizedPivot = new Vector2(0, 1);
            list.Translation = new Vector2(20, -20);
            list.Width = 320;
            list.Height = 450;
            list.ItemSize = 24;
            list.VirtualOverscan = 2;
            list.VirtualProvider = new VirtualCollectionFixtureProvider(itemCount);
            list.BlocksInputBehind = true;

            UIRuntimeCounters.Enabled = true;
            SceneNode countersNode = new(canvasRoot) { Name = "Virtual Collection Counters" };
            UIBoundableTransform countersTransform = countersNode.SetTransform<UIBoundableTransform>();
            countersTransform.MinAnchor = new Vector2(0, 1);
            countersTransform.MaxAnchor = new Vector2(0, 1);
            countersTransform.NormalizedPivot = new Vector2(0, 1);
            countersTransform.Translation = new Vector2(20, -480);
            countersTransform.Width = 600;
            countersTransform.Height = 64;
            UITextComponent countersText = countersNode.AddComponent<UITextComponent>()!;
            countersText.FontSize = 13;
            countersText.Color = ColorF4.White;
            countersNode.AddComponent<VirtualCollectionFixtureCounterComponent>()!.Initialize(
                list, (VirtualCollectionFixtureProvider)list.VirtualProvider, countersText);

            VirtualCollectionFixtureScrollComponent scroll = fixtureNode.AddComponent<VirtualCollectionFixtureScrollComponent>()!;
            scroll.List = list;
            scroll.ViewportHeight = 450;
        }
    }
}
