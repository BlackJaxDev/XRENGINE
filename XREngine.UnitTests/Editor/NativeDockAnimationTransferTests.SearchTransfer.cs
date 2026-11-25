using NUnit.Framework;
using XREngine.Editor;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Editor;

public sealed partial class NativeDockAnimationTransferTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void TwoHostTransferCopiesUnsubmittedDraftAndCaret(bool inactiveDestination)
    {
        using var fixture = new DockFixture();
        var clip = NewClip("Draft transfer");
        fixture.InstallLayouts();
        Host destination = fixture.CreateHost(fixture.World, fixture.DestinationKey, clip, plotZoom: 1.5f);
        if (inactiveDestination) destination.DeactivateAnimationTab(NativeDockPanel.Console);
        SetSelectedTarget(clip);
        Host source = fixture.CreateHost(fixture.World, fixture.SourceKey, clip, plotZoom: 2);
        SetPathSearch(source.Animation, "committed");
        var input = SearchInput(source.Animation);
        input.Text = "unsubmitted draft";
        input.CursorPosition = 9;
        input.SelectionAnchor = 2;
        Assert.That(Transfer(source, destination), Is.EqualTo(NativeDockTransferResult.Committed));
        var restored = SearchInput(destination.Animation);
        Assert.That((restored.Text, restored.CursorPosition, restored.SelectionAnchor),
            Is.EqualTo(("unsubmitted draft", 9, 2)));
        Assert.That(PathSearch(destination.Animation), Is.EqualTo("committed"));
    }

    [Test]
    public void TwoHostSaveFailureRestoresThePriorDraftAndSelection()
    {
        using var fixture = new DockFixture(failOnPathLookup: 2);
        var clip = NewClip("Draft compensation");
        fixture.InstallLayouts();
        Host destination = fixture.CreateHost(fixture.World, fixture.DestinationKey, clip, plotZoom: 1.5f);
        SetPathSearch(destination.Animation, "destination committed");
        SearchInput(destination.Animation).Text = "destination draft";
        SearchInput(destination.Animation).CursorPosition = 10;
        SearchInput(destination.Animation).SelectionAnchor = 3;
        destination.DeactivateAnimationTab(NativeDockPanel.Console);
        SetSelectedTarget(clip);
        Host source = fixture.CreateHost(fixture.World, fixture.SourceKey, clip, plotZoom: 2);
        SetPathSearch(source.Animation, "source committed");
        SearchInput(source.Animation).Text = "source draft";
        Assert.That(Transfer(source, destination), Is.EqualTo(NativeDockTransferResult.Rejected));
        var restored = SearchInput(destination.Animation);
        Assert.That((restored.Text, restored.CursorPosition, restored.SelectionAnchor),
            Is.EqualTo(("destination draft", 10, 3)));
        Assert.That(PathSearch(destination.Animation), Is.EqualTo("destination committed"));
        Assert.That(IsPanelOpen(source.Tree, NativeDockPanel.Animation), Is.True);
    }
    [TestCase(false)]
    [TestCase(true)]
    public void NormalSaveCallbackRejectionCompensatesOnlyTheUnchangedDestination(bool editDestination)
    {
        using var fixture = new DockFixture();
        var clip = NewClip("Callback compensation");
        fixture.InstallLayouts();
        Host destination = fixture.CreateHost(fixture.World, fixture.DestinationKey, clip, plotZoom: 1.5f);
        SetPathSearch(destination.Animation, "destination committed");
        SearchInput(destination.Animation).Text = "destination draft";
        SearchInput(destination.Animation).CursorPosition = 10;
        SearchInput(destination.Animation).SelectionAnchor = 3;
        destination.DeactivateAnimationTab(NativeDockPanel.Console);
        SetSelectedTarget(clip);
        Host source = fixture.CreateHost(fixture.World, fixture.SourceKey, clip, plotZoom: 2);
        SetPathSearch(source.Animation, "source committed");
        SearchInput(source.Animation).Text = "source draft";
        bool edited = false;
        fixture.Paths.OnLookup = lookup =>
        {
            if (lookup != 2) return;
            edited = true;
            SearchInput(editDestination ? destination.Animation : source.Animation).Text = "newer callback draft";
        };
        Assert.That(Transfer(source, destination), Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(edited, Is.True);
        Assert.That(IsPanelOpen(source.Tree, NativeDockPanel.Animation), Is.True);
        var restored = SearchInput(destination.Animation);
        if (editDestination)
        {
            Assert.That(restored.Text, Is.EqualTo("newer callback draft"));
            Assert.That(PathSearch(destination.Animation), Is.EqualTo("source committed"));
            Assert.That(PlotZoom(destination.Animation), Is.EqualTo(2));
        }
        else
        {
            Assert.That((restored.Text, restored.CursorPosition, restored.SelectionAnchor),
                Is.EqualTo(("destination draft", 10, 3)));
            Assert.That(PathSearch(destination.Animation), Is.EqualTo("destination committed"));
            Assert.That(PlotZoom(destination.Animation), Is.EqualTo(1.5f));
            Assert.That(SearchInput(source.Animation).Text, Is.EqualTo("newer callback draft"));
        }
    }

    [TestCase(false, "text")]
    [TestCase(true, "text")]
    [TestCase(false, "caret")]
    [TestCase(true, "caret")]
    [TestCase(false, "display")]
    [TestCase(true, "display")]
    [TestCase(false, "newer display")]
    public void FirstSavePreservesNewerEqualValueSearchIntent(bool inactive, string field)
    {
        using var fixture = new DockFixture();
        var clip = NewClip("Receive admission");
        fixture.InstallLayouts();
        Host destination = fixture.CreateHost(fixture.World, fixture.DestinationKey, clip, plotZoom: 1.5f);
        SetPathSearch(destination.Animation, "destination committed");
        var input = SearchInput(destination.Animation);
        input.Text = "destination draft";
        input.CursorPosition = 5;
        input.SelectionAnchor = 2;
        if (inactive) destination.DeactivateAnimationTab(NativeDockPanel.Console);
        SetSelectedTarget(clip);
        Host source = fixture.CreateHost(fixture.World, fixture.SourceKey, clip, plotZoom: 2);
        SearchInput(source.Animation).Text = "source draft";
        bool attempted = false;
        fixture.Paths.OnLookup = lookup =>
        {
            if (lookup != 1) return;
            attempted = true;
            if (field == "text") input.Text = input.Text;
            else if (field == "caret") input.CursorPosition = input.CursorPosition;
            else
            {
                var display = input.SceneNode.GetComponent<UITextComponent>()!;
                display.Text = field == "newer display" ? "newer callback display" : display.Text;
            }
        };
        Assert.That(Transfer(source, destination), Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(attempted, Is.True);
        Assert.That((input.Text, input.CursorPosition, input.SelectionAnchor),
            Is.EqualTo(("destination draft", 5, 2)));
        if (field == "newer display")
            Assert.That(input.SceneNode.GetComponent<UITextComponent>()!.Text, Is.EqualTo("newer callback display"));
        Assert.That(PathSearch(destination.Animation), Is.EqualTo("destination committed"));
        Assert.That(IsPanelOpen(source.Tree, NativeDockPanel.Animation), Is.True);
    }

    [TestCase("text", false)]
    [TestCase("caret", false)]
    [TestCase("display", false)]
    [TestCase("display", true)]
    public void ActivationDisplayWritePreservesNewerSearchIntent(string field, bool activeDestination)
    {
        using var fixture = new DockFixture();
        var clip = NewClip("Activation admission");
        fixture.InstallLayouts();
        Host destination = fixture.CreateHost(fixture.World, fixture.DestinationKey, clip, plotZoom: 1.5f);
        SetPathSearch(destination.Animation, "destination committed");
        var input = SearchInput(destination.Animation);
        input.Text = "destination draft";
        input.CursorPosition = 5;
        var display = input.SceneNode.GetComponent<UITextComponent>()!;
        display.Text = "retained display";
        if (!activeDestination) destination.DeactivateAnimationTab(NativeDockPanel.Console);
        SetSelectedTarget(clip);
        Host source = fixture.CreateHost(fixture.World, fixture.SourceKey, clip, plotZoom: 2);
        SearchInput(source.Animation).Text = "source draft";
        bool attempted = false;
        display.PropertyChanging += (_, change) =>
        {
            if (attempted || change.PropertyName != nameof(UITextComponent.Text)) return;
            attempted = true;
            if (field == "text") input.Text = "newer activation draft";
            else if (field == "caret") input.CursorPosition = input.CursorPosition;
            else display.Text = "newer activation display";
        };
        Assert.That(Transfer(source, destination), Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(attempted, Is.True);
        Assert.That(input.Text, Is.EqualTo(field == "text" ? "newer activation draft" : "destination draft"));
        Assert.That(display.Text, Is.EqualTo(field == "text" ? "newer activation draft" :
            field == "display" ? "newer activation display" : "retained display"));
        Assert.That(PathSearch(destination.Animation), Is.EqualTo("destination committed"));
        Assert.That(IsPanelOpen(source.Tree, NativeDockPanel.Animation), Is.True);
    }

}
