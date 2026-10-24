using System.Reflection;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public class NativeOwnerSubscriptionTeardownTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Test]
    public void ProjectBrowser_DirectDestroyDetachesTheExactSubscribedFileFamily()
    {
        SceneNode node = new("Browser") { IsActiveSelf = false };
        node.SetTransform<UIScrollingTransform>();
        UIProjectBrowserComponent browser = node.AddComponent<UIProjectBrowserComponent>()!;
        Action<XREngine.Core.Files.AssetFileChangeEventArgs> handler = BrowserChanged(browser);
        try
        {
            // Model an already active subscription without starting a file watcher.
            typeof(UIProjectBrowserComponent).GetField("_ownerActive", Fields)!.SetValue(browser, true);
            typeof(UIProjectBrowserComponent).GetField("_subscribedFamily", Fields)!.SetValue(browser,
                EProjectBrowserFileEventFamily.Game);
            Engine.Assets.GameFileChanged += handler;
            browser.ShowEngineFiles = true;
            Assert.That(HasHandler(Engine.Assets, "GameFileChanged", browser), Is.False);
            Assert.That(HasHandler(Engine.Assets, "EngineFileChanged", browser), Is.False);
            browser.Destroy(true);
            Assert.That(HasHandler(Engine.Assets, "GameFileChanged", browser), Is.False);
        }
        finally
        {
            Engine.Assets.GameFileChanged -= handler;
            node.Destroy(true);
        }
    }

    [Test]
    public void DockRegistration_ConfigureReleasesOnlyItsOldKeyAndDestroyReleasesCurrentKey()
    {
        SceneNode node = new("Dock") { IsActiveSelf = false };
        NativeDockWindowRegistration host = node.AddComponent<NativeDockWindowRegistration>()!;
        const string oldKey = "teardown-old";
        const string newKey = "teardown-new";
        try
        {
            host.Configure(oldKey, _ => { });
            NativeDockWindowRegistry.Register(oldKey, host);
            host.Configure(newKey, _ => { });
            Assert.That(Registry().ContainsKey(oldKey), Is.False);
            NativeDockWindowRegistry.Register(newKey, host);
            host.Destroy(true);
            Assert.That(Registry().ContainsKey(newKey), Is.False);
            host.Configure("late", _ => { });
            Assert.That(Registry().ContainsKey("late"), Is.False);
        }
        finally
        {
            NativeDockWindowRegistry.Unregister(oldKey, host);
            NativeDockWindowRegistry.Unregister(newKey, host);
            node.Destroy(true);
        }
    }

    [Test]
    public void ConsolePanel_DirectDestroyDetachesEntryEventAndRetainedVirtualProvider()
    {
        SceneNode node = new("Console") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        ConsolePanel panel = node.AddComponent<ConsolePanel>()!;
        UIListTransform list = new SceneNode(node, "Rows").SetTransform<UIListTransform>();
        UIVirtualTreeProvider provider = new(() => new UIBoundableTransform(),
            (_, _, _, _) => { }, (_, _, _) => { });
        list.VirtualProvider = provider;
        typeof(NativeVirtualTextPanel).GetField("_list", Fields)!.SetValue(panel, list);
        Action<LogEntry> handler = (Action<LogEntry>)Delegate.CreateDelegate(typeof(Action<LogEntry>), panel,
            typeof(ConsolePanel).GetMethod("EntryAdded", Fields)!);
        Debug.ConsoleEntryAdded += handler;
        typeof(ConsolePanel).GetField("_listening", Fields)!.SetValue(panel, true);
        int filterNotifications = 0;
        int scrollNotifications = 0;
        panel.FilterChanged += _ => filterNotifications++;
        panel.ScrollOffsetChanged += _ => scrollNotifications++;
        try
        {
            float inactiveOffset = list.ContentScrollOffset;
            panel.ScrollOffset = 42;
            panel.HandleMouseScroll(-1);
            Assert.That(panel.ScrollOffset, Is.EqualTo(42));
            Assert.That(list.ContentScrollOffset, Is.EqualTo(inactiveOffset));
            panel.Destroy(true);
            float retainedOffset = list.ContentScrollOffset;
            panel.Filter = "late";
            panel.ScrollOffset = 120;
            panel.HandleMouseScroll(-1);
            Assert.Multiple(() =>
            {
                Assert.That(HasHandler(typeof(Debug), "ConsoleEntryAdded", panel), Is.False);
                Assert.That(list.VirtualProvider, Is.Null);
                Assert.That(list.IsDestroyed, Is.False);
                Assert.That(list.ContentScrollOffset, Is.EqualTo(retainedOffset));
                Assert.That(filterNotifications, Is.Zero);
                Assert.That(scrollNotifications, Is.Zero);
                Assert.That(typeof(ConsolePanel).GetField("FilterChanged", Fields)!.GetValue(panel), Is.Null);
                Assert.That(typeof(NativeVirtualTextPanel).GetField("ScrollOffsetChanged", Fields)!.GetValue(panel), Is.Null);
            });
        }
        finally
        {
            Debug.ConsoleEntryAdded -= handler;
            node.Destroy(true);
        }
    }

    private static Action<XREngine.Core.Files.AssetFileChangeEventArgs> BrowserChanged(UIProjectBrowserComponent browser)
        => (Action<XREngine.Core.Files.AssetFileChangeEventArgs>)Delegate.CreateDelegate(
            typeof(Action<XREngine.Core.Files.AssetFileChangeEventArgs>), browser,
            typeof(UIProjectBrowserComponent).GetMethod("FileChanged", Fields)!);

    private static bool HasHandler(object source, string name, object owner)
        => (source.GetType().GetField(name, Fields)?.GetValue(source) as Delegate)?
            .GetInvocationList().Any(item => ReferenceEquals(item.Target, owner)) == true;

    private static bool HasHandler(Type source, string name, object owner)
        => (source.GetField(name, Fields)?.GetValue(null) as Delegate)?
            .GetInvocationList().Any(item => ReferenceEquals(item.Target, owner)) == true;

    private static Dictionary<string, NativeDockWindowRegistration> Registry()
        => (Dictionary<string, NativeDockWindowRegistration>)typeof(NativeDockWindowRegistry)
            .GetField("Hosts", Fields)!.GetValue(null)!;
}
