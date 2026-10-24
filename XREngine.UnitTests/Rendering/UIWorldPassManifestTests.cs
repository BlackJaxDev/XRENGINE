using System;
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
        manifest.TryCapture(root, out string? captureFailure).ShouldBeTrue(captureFailure);
        manifest.SetExpectedUiDraws(selected, 1).ShouldBeTrue();
        manifest.SetExpectedUiDraws(pruned, 1).ShouldBeTrue();
        manifest.RecordBranch(choice, selectedTrue: true);
        manifest.RecordExecuted(selected);
        manifest.RecordExecuted(choice);
        manifest.TryValidateExecutionStructure(out string? validationFailure)
            .ShouldBeTrue(validationFailure);
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
        manifest.TryCapture(root, out _).ShouldBeTrue();
        manifest.SetExpectedUiDraws(empty, 0).ShouldBeTrue();
        manifest.SetExpectedUiDraws(required, 1).ShouldBeTrue();

        manifest.RecordSkipped(empty);
        manifest.TryValidateExecutionStructure(out string? failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain("did not complete");
    }

    [Test]
    public void DelegateMutationAndExecutionFailurePoisonTheAttempt()
    {
        var root = new ViewportRenderCommandContainer();
        VPRC_IfElse choice = root.Add<VPRC_IfElse>();
        Func<bool> original = static () => true;
        choice.ConditionEvaluator = original;
        var manifest = new UIWorldPassManifest();
        manifest.TryCapture(root, out _).ShouldBeTrue();
        choice.ConditionEvaluator = static () => false;

        manifest.RecordBranch(choice, selectedTrue: true);
        manifest.TryValidateExecutionStructure(out string? failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain("configuration changed");

        choice.ConditionEvaluator = original;
        manifest.TryCapture(root, out _).ShouldBeTrue();
        manifest.RecordBranch(choice, selectedTrue: true);
        manifest.RecordFailure(choice);
        manifest.TryValidateExecutionStructure(out failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain("failed");
    }

    [Test]
    public void DynamicRepeatIsRejectedBeforeAuthoring()
    {
        var root = new ViewportRenderCommandContainer();
        root.Add<VPRC_Repeat>();
        var manifest = new UIWorldPassManifest();

        manifest.TryCapture(root, out string? failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain(nameof(VPRC_Repeat));
    }

    [Test]
    public void AppendingToAnEmptyRootInvalidatesTheClosedGraph()
    {
        var root = new ViewportRenderCommandContainer();
        var manifest = new UIWorldPassManifest();
        manifest.TryCapture(root, out _).ShouldBeTrue();

        root.Add<VPRC_RenderMeshesPass>();

        manifest.TryValidateExecutionStructure(out string? failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain("root command count changed");
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
        manifest.TryCapture(root, out _).ShouldBeTrue();
        manifest.RecordBranch(choice, selectedTrue: true);
        manifest.RecordExecuted(choice);

        pruned.Add<VPRC_RenderMeshesPass>();

        manifest.TryValidateExecutionStructure(out string? failure).ShouldBeFalse();
        failure.ShouldNotBeNull();
        failure.ShouldContain("branch configuration changed");
    }

    [Test]
    public void PacketClaimStaysAliveAcrossPassesAndRejectsASecondPacket()
    {
        var root = new ViewportRenderCommandContainer();
        var bounds = new BoundingRectangleF(Vector2.Zero, Vector2.One);
        var packet = new UIPublication(1, 1, 1, 1, 1, default, default, [], [],
            bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        var other = new UIPublication(2, 1, 1, 1, 2, default, default, [], [],
            bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        var manifest = new UIWorldPassManifest();
        bool initialClaimReleased = false;
        try
        {
            manifest.TryCapture(root, out _).ShouldBeTrue();
            manifest.TryBindPublication(packet).ShouldBeTrue();
            packet.Release();
            initialClaimReleased = true;
            packet.Retain();
            packet.Release();
            manifest.TryBindPublication(other).ShouldBeFalse();
        }
        finally
        {
            manifest.Dispose();
            if (!initialClaimReleased)
                packet.Release();
            other.Release();
        }
        Should.Throw<ObjectDisposedException>(packet.Retain);
    }
}
