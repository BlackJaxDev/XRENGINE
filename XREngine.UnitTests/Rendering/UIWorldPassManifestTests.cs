using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIWorldPassManifestTests
{
    [Test]
    public void SelectedBranchRequiresItsPassAndPrunesTheOtherBranch()
    {
        var root = new ViewportRenderCommandContainer();
        VPRC_IfElse choice = root.Add<VPRC_IfElse>();
        var trueCommands = new ViewportRenderCommandContainer();
        var falseCommands = new ViewportRenderCommandContainer();
        VPRC_RenderMeshesPass selected = trueCommands.Add<VPRC_RenderMeshesPass>();
        VPRC_RenderMeshesPass pruned = falseCommands.Add<VPRC_RenderMeshesPass>();
        selected.RenderPass = 4;
        pruned.RenderPass = 5;
        choice.TrueCommands = trueCommands;
        choice.FalseCommands = falseCommands;

        var manifest = new UIWorldPassManifest();
        manifest.TryReserveAttempt(out ulong token).ShouldBeTrue();
        manifest.TryCaptureReserved(root, token, out string? captureFailure).ShouldBeTrue(captureFailure);
        manifest.SetExpectedUiDraws(selected, 1).ShouldBeTrue();
        manifest.SetExpectedUiDraws(pruned, 1).ShouldBeTrue();
        manifest.RecordBranch(choice, selectedTrue: true);
        manifest.RecordExecuted(selected);
        manifest.RecordExecuted(choice);
        manifest.TryValidateExecutionStructure(out string? validationFailure)
            .ShouldBeTrue(validationFailure);
        manifest.ReleaseAttempt(token).ShouldBeTrue();
    }

    [Test]
    public void EmptySelectedPassCanSkipButMissingRequiredPassFails()
    {
        var root = new ViewportRenderCommandContainer();
        VPRC_RenderMeshesPass empty = root.Add<VPRC_RenderMeshesPass>();
        VPRC_RenderMeshesPass required = root.Add<VPRC_RenderMeshesPass>();
        empty.RenderPass = 2;
        required.RenderPass = 3;
        var manifest = new UIWorldPassManifest();
        manifest.TryReserveAttempt(out ulong token).ShouldBeTrue();
        manifest.TryCaptureReserved(root, token, out _).ShouldBeTrue();
        manifest.SetExpectedUiDraws(empty, 0).ShouldBeTrue();
        manifest.SetExpectedUiDraws(required, 1).ShouldBeTrue();

        manifest.RecordSkipped(empty);
        manifest.TryValidateExecutionStructure(out string? failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain("did not complete");
        manifest.ReleaseAttempt(token).ShouldBeTrue();
    }

    [Test]
    public void DelegateMutationAndExecutionFailurePoisonTheAttempt()
    {
        var root = new ViewportRenderCommandContainer();
        VPRC_IfElse choice = root.Add<VPRC_IfElse>();
        Func<bool> original = static () => true;
        choice.ConditionEvaluator = original;
        var manifest = new UIWorldPassManifest();
        manifest.TryReserveAttempt(out ulong token).ShouldBeTrue();
        manifest.TryCaptureReserved(root, token, out _).ShouldBeTrue();
        choice.ConditionEvaluator = static () => false;

        manifest.RecordBranch(choice, selectedTrue: true);
        manifest.TryValidateExecutionStructure(out string? failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain("configuration changed");

        choice.ConditionEvaluator = original;
        manifest.ReleaseAttempt(token).ShouldBeTrue();
        manifest.TryReserveAttempt(out token).ShouldBeTrue();
        manifest.TryCaptureReserved(root, token, out _).ShouldBeTrue();
        manifest.RecordBranch(choice, selectedTrue: true);
        manifest.RecordFailure(choice);
        manifest.TryValidateExecutionStructure(out failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain("failed");
        manifest.ReleaseAttempt(token).ShouldBeTrue();
    }

    [Test]
    public void DynamicRepeatIsRejectedBeforeAuthoring()
    {
        var root = new ViewportRenderCommandContainer();
        root.Add<VPRC_Repeat>();
        var manifest = new UIWorldPassManifest();

        manifest.TryReserveAttempt(out ulong token).ShouldBeTrue();
        manifest.TryCaptureReserved(root, token, out string? failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain(nameof(VPRC_Repeat));
        manifest.ReleaseAttempt(token).ShouldBeTrue();
    }

    [Test]
    public void AppendingToAnEmptyRootInvalidatesTheClosedGraph()
    {
        var root = new ViewportRenderCommandContainer();
        var manifest = new UIWorldPassManifest();
        manifest.TryReserveAttempt(out ulong token).ShouldBeTrue();
        manifest.TryCaptureReserved(root, token, out _).ShouldBeTrue();

        root.Add<VPRC_RenderMeshesPass>();

        manifest.TryValidateExecutionStructure(out string? failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain("root command count changed");
        manifest.ReleaseAttempt(token).ShouldBeTrue();
    }

    [Test]
    public void AppendingToAPrunedEmptyBranchInvalidatesTheClosedGraph()
    {
        var root = new ViewportRenderCommandContainer();
        VPRC_IfElse choice = root.Add<VPRC_IfElse>();
        var selected = new ViewportRenderCommandContainer();
        var pruned = new ViewportRenderCommandContainer();
        choice.TrueCommands = selected;
        choice.FalseCommands = pruned;
        var manifest = new UIWorldPassManifest();
        manifest.TryReserveAttempt(out ulong token).ShouldBeTrue();
        manifest.TryCaptureReserved(root, token, out _).ShouldBeTrue();
        manifest.RecordBranch(choice, selectedTrue: true);
        manifest.RecordExecuted(choice);

        pruned.Add<VPRC_RenderMeshesPass>();

        manifest.TryValidateExecutionStructure(out string? failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain("branch configuration changed");
        manifest.ReleaseAttempt(token).ShouldBeTrue();
    }

    [Test]
    public void PacketClaimsAllowSeparateCanvasesAndRejectAChangedCanvasPacket()
    {
        var root = new ViewportRenderCommandContainer();
        var bounds = new BoundingRectangleF(Vector2.Zero, Vector2.One);
        var packet = new UIPublication(1, 1, 1, 1, 1, default, default, [], [],
            bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        var other = new UIPublication(2, 1, 1, 1, 2, default, default, [], [],
            bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        var changed = new UIPublication(3, 1, 1, 1, 3, default, default, [], [],
            bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        var manifest = new UIWorldPassManifest();
        bool initialClaimReleased = false;
        bool otherInitialClaimReleased = false;
        object canvasA = new();
        object canvasB = new();
        ulong token = 0;
        try
        {
            manifest.TryReserveAttempt(out token).ShouldBeTrue();
            manifest.TryCaptureReserved(root, token, out _).ShouldBeTrue();
            manifest.TryBindPublication(canvasA, packet).ShouldBeTrue();
            manifest.TryBindPublication(canvasB, other).ShouldBeTrue();
            manifest.SealPacketSet().ShouldBeTrue();
            other.Release();
            otherInitialClaimReleased = true;
            manifest.TryGetPublication(canvasA, out UIPublication? pinned).ShouldBeTrue();
            pinned.ShouldBeSameAs(packet);
            manifest.TryGetPublication(canvasB, out pinned).ShouldBeTrue();
            pinned.ShouldBeSameAs(other);
            packet.Release();
            initialClaimReleased = true;
            packet.Retain();
            packet.Release();
            manifest.TryBindPublication(canvasA, changed).ShouldBeFalse();
            manifest.TryBindPublication(new object(), changed).ShouldBeFalse();
        }
        finally
        {
            if (token != 0)
                manifest.ReleaseAttempt(token).ShouldBeTrue();
            if (!initialClaimReleased)
                packet.Release();
            if (!otherInitialClaimReleased)
                other.Release();
            changed.Release();
        }
        Should.Throw<ObjectDisposedException>(packet.Retain);
        Should.Throw<ObjectDisposedException>(other.Retain);
    }

    [Test]
    public void ReservedAttemptRejectsReentrantCaptureWithoutResettingItsGraph()
    {
        var root = new ViewportRenderCommandContainer();
        VPRC_RenderMeshesPass pass = root.Add<VPRC_RenderMeshesPass>();
        var manifest = new UIWorldPassManifest();
        manifest.TryReserveAttempt(out ulong token).ShouldBeTrue();
        manifest.TryCaptureReserved(root, token, out _).ShouldBeTrue();
        manifest.TryReserveAttempt(out _).ShouldBeFalse();
        manifest.TryCaptureReserved(root, token, out _).ShouldBeFalse();
        manifest.SetExpectedUiDraws(pass, 1).ShouldBeTrue();
        manifest.RecordExecuted(pass);
        manifest.TryValidateExecutionStructure(out _).ShouldBeTrue();
        manifest.ReleaseAttempt(token).ShouldBeTrue();
    }

    [Test]
    public void NestedWindowScopesRestoreTheEarlierAttempt()
    {
        var root = new ViewportRenderCommandContainer();
        var first = new UIWorldPassManifest();
        var second = new UIWorldPassManifest();
        first.TryReserveAttempt(out ulong firstToken).ShouldBeTrue();
        second.TryReserveAttempt(out ulong secondToken).ShouldBeTrue();
        first.TryCaptureReserved(root, firstToken, out _).ShouldBeTrue();
        second.TryCaptureReserved(root, secondToken, out _).ShouldBeTrue();
        first.SealPacketSet().ShouldBeTrue();
        second.SealPacketSet().ShouldBeTrue();

        using (first.EnterScope(firstToken))
        {
            UIWorldPassManifest.Active.ShouldBeSameAs(first);
            using (second.EnterScope(secondToken))
                UIWorldPassManifest.Active.ShouldBeSameAs(second);
            UIWorldPassManifest.Active.ShouldBeSameAs(first);
        }
        UIWorldPassManifest.Active.ShouldBeNull();
        first.ReleaseAttempt(firstToken).ShouldBeTrue();
        second.ReleaseAttempt(secondToken).ShouldBeTrue();
    }

    [Test]
    public void SwitchSelectsOneFrozenCaseAndPrunesOtherCases()
    {
        var root = new ViewportRenderCommandContainer();
        VPRC_Switch choice = root.Add<VPRC_Switch>();
        var selected = new ViewportRenderCommandContainer();
        var pruned = new ViewportRenderCommandContainer();
        VPRC_RenderMeshesPass selectedPass = selected.Add<VPRC_RenderMeshesPass>();
        pruned.Add<VPRC_RenderMeshesPass>();
        choice.Cases = new Dictionary<int, ViewportRenderCommandContainer>
        {
            [7] = selected,
            [8] = pruned,
        };
        var manifest = new UIWorldPassManifest();
        manifest.TryReserveAttempt(out ulong token).ShouldBeTrue();
        manifest.TryCaptureReserved(root, token, out _).ShouldBeTrue();
        manifest.SetExpectedUiDraws(selectedPass, 1).ShouldBeTrue();
        manifest.RecordSwitch(choice, 7, selected);
        manifest.RecordExecuted(selectedPass);
        manifest.RecordExecuted(choice);
        manifest.TryValidateExecutionStructure(out string? failure).ShouldBeTrue(failure);
        manifest.ReleaseAttempt(token).ShouldBeTrue();
    }

    [Test]
    public void MutatingASwitchCaseMapPoisonsTheClosedGraph()
    {
        var root = new ViewportRenderCommandContainer();
        VPRC_Switch choice = root.Add<VPRC_Switch>();
        var selected = new ViewportRenderCommandContainer();
        var cases = new Dictionary<int, ViewportRenderCommandContainer> { [7] = selected };
        choice.Cases = cases;
        var manifest = new UIWorldPassManifest();
        manifest.TryReserveAttempt(out ulong token).ShouldBeTrue();
        manifest.TryCaptureReserved(root, token, out _).ShouldBeTrue();
        cases[7] = new ViewportRenderCommandContainer();

        manifest.RecordSwitch(choice, 7, cases[7]);
        manifest.TryValidateExecutionStructure(out string? failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain("switch case map changed");
        manifest.ReleaseAttempt(token).ShouldBeTrue();
    }

    [Test]
    public void SwitchDefaultCaseCanProveAnEmptySelectedPass()
    {
        var root = new ViewportRenderCommandContainer();
        VPRC_Switch choice = root.Add<VPRC_Switch>();
        choice.Cases = new Dictionary<int, ViewportRenderCommandContainer>
        {
            [7] = new ViewportRenderCommandContainer(),
        };
        var defaultCommands = new ViewportRenderCommandContainer();
        VPRC_RenderMeshesPass empty = defaultCommands.Add<VPRC_RenderMeshesPass>();
        choice.DefaultCase = defaultCommands;
        var manifest = new UIWorldPassManifest();
        manifest.TryReserveAttempt(out ulong token).ShouldBeTrue();
        manifest.TryCaptureReserved(root, token, out _).ShouldBeTrue();
        manifest.SetExpectedUiDraws(empty, 0).ShouldBeTrue();
        manifest.RecordSwitch(choice, 99, defaultCommands);
        manifest.RecordSkipped(empty);
        manifest.RecordExecuted(choice);
        manifest.TryValidateExecutionStructure(out string? failure).ShouldBeTrue(failure);
        manifest.ReleaseAttempt(token).ShouldBeTrue();
    }
}
