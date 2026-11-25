using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeDockRenderGateTests
{
    [Test]
    public void NestedRequestRunsOneMorePassAfterTheCurrentPass()
    {
        NativeDockRenderGate gate = new();
        List<string> events = [];
        int passes = 0;
        gate.Run(() =>
        {
            passes++;
            events.Add($"start {passes}");
            if (passes == 1)
            {
                // A callback during the pass asks twice; the requests coalesce.
                gate.Run(() => events.Add("nested")).ShouldBeFalse();
                gate.Run(() => events.Add("nested")).ShouldBeFalse();
            }
            events.Add($"end {passes}");
        }).ShouldBeTrue();

        events.ShouldBe(["start 1", "end 1", "start 2", "end 2"]);
        gate.Revision.ShouldBe(2UL);
        gate.IsRendering.ShouldBeFalse();
    }

    [Test]
    public void CallbacksFromAnEarlierPassAreNotCurrent()
    {
        NativeDockRenderGate gate = new();
        ulong first = 0;
        bool currentDuringPass = true;
        gate.Run(() =>
        {
            first = gate.Revision;
            currentDuringPass = gate.IsCurrent(first);
        });
        currentDuringPass.ShouldBeFalse();
        gate.IsCurrent(first).ShouldBeTrue();

        gate.Run(() => { });
        gate.IsCurrent(first).ShouldBeFalse();
        gate.IsCurrent(gate.Revision).ShouldBeTrue();
    }

    [Test]
    public void EndlessRequestsStopAndLeaveTheGateUsable()
    {
        NativeDockRenderGate gate = new();
        int passes = 0;
        Should.Throw<InvalidOperationException>(() => gate.Run(() =>
        {
            passes++;
            gate.Run(() => { });
        }));
        passes.ShouldBe(NativeDockRenderGate.MaximumPasses);
        gate.IsRendering.ShouldBeFalse();

        gate.Run(() => passes++).ShouldBeTrue();
        passes.ShouldBe(NativeDockRenderGate.MaximumPasses + 1);
    }

    [Test]
    public void FailedPassReleasesTheGate()
    {
        NativeDockRenderGate gate = new();
        Should.Throw<InvalidOperationException>(() =>
            gate.Run(() => throw new InvalidOperationException("Pass failed")));
        gate.IsRendering.ShouldBeFalse();
        gate.Run(() => { }).ShouldBeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedRebuildRetiresCallbacksFromItsPartialControls(bool repeatedRequests)
    {
        NativeDockRenderGate gate = new();
        ulong failedRevision = 0;
        Should.Throw<InvalidOperationException>(() => gate.Run(() =>
        {
            failedRevision = gate.Revision;
            if (repeatedRequests)
                gate.Run(() => { });
            else
                throw new InvalidOperationException("A control failed during construction.");
        }));
        gate.IsRendering.ShouldBeFalse();
        gate.IsCurrent(failedRevision).ShouldBeFalse();

        gate.Run(() => { }).ShouldBeTrue();
        gate.IsCurrent(failedRevision).ShouldBeFalse();
        gate.IsCurrent(gate.Revision).ShouldBeTrue();
    }

    [Test]
    public void ClosingTheLastTabKeepsAReopenableStack()
    {
        NativeDockTree tree = new()
        {
            Root = new NativeDockNode { Tabs = [NativeDockPanel.Console], Selected = NativeDockPanel.Console },
        };
        tree.Sanitize();
        string rootId = tree.Root.Id;

        tree.Close(NativeDockPanel.Console).ShouldBeTrue();

        tree.Root.Id.ShouldBe(rootId);
        tree.Root.IsSplit.ShouldBeFalse();
        tree.Root.Tabs.ShouldBeEmpty();
        tree.Root.Selected.ShouldBeNull();
        tree.ClosedPanels.ShouldContain(NativeDockPanel.Console);
        tree.Sanitize();
        tree.Reopen(NativeDockPanel.Console, rootId).ShouldBeTrue();
        tree.Root.Tabs.ShouldBe([NativeDockPanel.Console]);
        tree.Root.Selected.ShouldBe(NativeDockPanel.Console);
    }

    [Test]
    public void ClosingEveryTabInASplitCollapsesToOneEmptyStack()
    {
        NativeDockTree tree = new NativeDockLayout().Sanitize().DockTree!;
        foreach (string panel in tree.Stacks().SelectMany(stack => stack.Tabs).ToArray())
            tree.Close(panel).ShouldBeTrue();

        tree.Root.IsSplit.ShouldBeFalse();
        tree.Root.Tabs.ShouldBeEmpty();
        tree.Sanitize();
        tree.ClosedPanels.Count.ShouldBe(NativeDockPanel.All.Length);
        tree.Reopen(NativeDockPanel.Scene, tree.Root.Id).ShouldBeTrue();
    }
}
