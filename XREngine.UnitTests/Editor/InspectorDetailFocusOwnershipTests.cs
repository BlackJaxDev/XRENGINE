using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class InspectorDetailFocusOwnershipTests
{
    [Test]
    public void DetailFocusRestore_AcceptsItsOwnFocusIntent()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, input, control) = CreateFocusedDetail();
        try
        {
            CaptureAndClose(panel);
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(Field<ulong>(panel, "_savedDetailReleasedIntentRevision"), Is.EqualTo(input.FocusIntentRevision));

            Invoke(panel, "RestoreViewState");
            Invoke(panel, "RestoreDetailFocusTick");

            Assert.That(input.FocusedComponent, Is.Not.Null);
            Assert.That(input.FocusedComponent, Is.Not.SameAs(control));
            Assert.That(Field<int>(panel, "_pendingFocusFrames"), Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DetailFocusRestore_RejectsNewerNullOrPendingFocus(bool pending)
    {
        using UITestShaderScope shaders = new();
        var (root, panel, input, _) = CreateFocusedDetail();
        try
        {
            CaptureAndClose(panel);
            if (pending)
                input.HasPendingFocusRequest = true;
            else
                input.FocusedComponent = null;

            Invoke(panel, "RestoreViewState");
            Assert.That(Field<int>(panel, "_pendingFocusFrames"), Is.Zero);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DetailFocusRestore_RejectsFocusChangedAfterCapture()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, input, _) = CreateFocusedDetail();
        try
        {
            Invoke(panel, "CaptureViewState");
            input.FocusedComponent = null;
            Invoke(panel, "HideDetailEditor");

            Assert.That(Field<ulong>(panel, "_savedDetailReleasedIntentRevision"), Is.Zero);
            Invoke(panel, "RestoreViewState");
            Assert.That(Field<int>(panel, "_pendingFocusFrames"), Is.Zero);
            Assert.That(input.FocusedComponent, Is.Null);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DetailCleanup_PreservesReplacementAttachedDuringDeactivation()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        try
        {
            SceneNode oldEditor = Field<SceneNode>(panel, "_detailEditor");
            UIBoundableTransform detailTransform = Field<UIBoundableTransform>(panel, "_detailTransform");
            SceneNode? replacement = null;
            oldEditor.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(SceneNode.IsActiveSelf) || replacement is not null)
                    return;
                replacement = detailTransform.SceneNode!.NewChild();
                SetField(panel, "_detailEditor", replacement);
            };

            Invoke(panel, "HideDetailEditor");

            Assert.That(replacement, Is.Not.Null);
            Assert.That(replacement!.IsDestroyed, Is.False);
            Assert.That(Field<SceneNode>(panel, "_detailEditor"), Is.SameAs(replacement));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DetailOpen_PendingFocusKeepsCurrentEditor()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, input, _) = CreateFocusedDetail();
        try
        {
            SceneNode current = Field<SceneNode>(panel, "_detailEditor");
            InspectorListDetailRefreshComponent refresh = current.AddComponent<InspectorListDetailRefreshComponent>()!;
            int refreshed = 0;
            refresh.Refresh = () => refreshed++;
            InspectorStringDetailSizeComponent stringSize = current.GetComponent<InspectorStringDetailSizeComponent>()!;
            int childCount = Field<UIBoundableTransform>(panel, "_detailTransform").SceneNode!.Transform.ChildCount;
            input.HasPendingFocusRequest = true;

            Invoke(panel, "ShowDetailEditor", OtherProperty());

            Assert.That(Field<SceneNode>(panel, "_detailEditor"), Is.SameAs(current));
            Assert.That(current.IsDestroyed, Is.False);
            refresh.Refresh?.Invoke();
            Assert.That(refreshed, Is.EqualTo(1));
            Assert.That(Field<bool>(stringSize, "_retired"), Is.False);
            Assert.That(Field<UIBoundableTransform>(panel, "_detailTransform").SceneNode!.Transform.ChildCount,
                Is.EqualTo(childCount));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DetailOpen_ReplacementDuringDeactivationIsNotOverwritten()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        try
        {
            SceneNode current = Field<SceneNode>(panel, "_detailEditor");
            UIBoundableTransform detailTransform = Field<UIBoundableTransform>(panel, "_detailTransform");
            SceneNode? replacement = null;
            current.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(SceneNode.IsActiveSelf) || replacement is not null)
                    return;
                replacement = detailTransform.SceneNode!.NewChild();
                SetField(panel, "_detailEditor", replacement);
            };

            Invoke(panel, "ShowDetailEditor", OtherProperty());

            Assert.That(replacement, Is.Not.Null);
            Assert.That(replacement!.IsDestroyed, Is.False);
            Assert.That(Field<SceneNode>(panel, "_detailEditor"), Is.SameAs(replacement));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DetailOpen_DestroyVetoKeepsCurrentEditor()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        SceneNode current = Field<SceneNode>(panel, "_detailEditor");
        Func<XRObjectBase, bool> veto = _ => false;
        current.Destroying += veto;
        try
        {
            int childCount = Field<UIBoundableTransform>(panel, "_detailTransform").SceneNode!.Transform.ChildCount;
            InspectorListDetailRefreshComponent refresh = current.AddComponent<InspectorListDetailRefreshComponent>()!;
            int refreshed = 0;
            refresh.Refresh = () => refreshed++;
            InspectorStringDetailSizeComponent stringSize = current.GetComponent<InspectorStringDetailSizeComponent>()!;

            Invoke(panel, "ShowDetailEditor", OtherProperty());

            Assert.That(Field<SceneNode>(panel, "_detailEditor"), Is.SameAs(current));
            Assert.That(current.IsDestroyed, Is.False);
            Assert.That(current.IsActiveSelf, Is.True);
            refresh.Refresh?.Invoke();
            Assert.That(refreshed, Is.EqualTo(1));
            Assert.That(Field<bool>(stringSize, "_retired"), Is.False);
            Assert.That(Field<UIBoundableTransform>(panel, "_detailTransform").SceneNode!.Transform.ChildCount,
                Is.EqualTo(childCount));
        }
        finally
        {
            current.Destroying -= veto;
            root.Destroy(true);
        }
    }

    [Test]
    public void TargetChange_PendingFocusShowsNewPropertiesAndRetiresOldDetailWhenReady()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, input, oldControl) = CreateFocusedDetail();
        try
        {
            SceneNode oldEditor = Field<SceneNode>(panel, "_detailEditor");
            object?[] newer = [new InspectorPreviewTarget { Value = "new" }];
            input.HasPendingFocusRequest = true;

            panel.InspectedObjects = newer;

            Assert.That(ProviderTargets(panel), Is.SameAs(newer));
            Assert.That(oldControl.IsActiveInHierarchy, Is.False);
            Assert.That(Field<UIScrollableTransform>(panel, "_propertyScroll").Visibility,
                Is.EqualTo(EVisibility.Visible));
            Assert.That(Field<bool>(panel, "_targetRefreshPending"), Is.True);

            input.HasPendingFocusRequest = false;
            Invoke(panel, "RetryDeferredTargetRefresh");

            Assert.That(oldEditor.IsDestroyed, Is.True);
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(ProviderTargets(panel), Is.SameAs(newer));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void TargetChange_DestroyVetoShowsNewPropertiesWithoutRepeatedRetirement()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, oldControl) = CreateFocusedDetail();
        SceneNode oldEditor = Field<SceneNode>(panel, "_detailEditor");
        int attempts = 0;
        Func<XRObjectBase, bool> veto = _ => { attempts++; return false; };
        oldEditor.Destroying += veto;
        try
        {
            object?[] newer = [new InspectorPreviewTarget { Value = "new" }];
            panel.InspectedObjects = newer;

            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(oldControl.IsActiveInHierarchy, Is.False);
            Assert.That(ProviderTargets(panel), Is.SameAs(newer));
            Assert.That(Field<UIScrollableTransform>(panel, "_propertyScroll").Visibility,
                Is.EqualTo(EVisibility.Visible));
            Assert.That(Field<bool>(panel, "_targetRefreshPending"), Is.False);
            Assert.That(oldEditor.IsDestroyed, Is.False);

            oldEditor.Destroying -= veto;
            object?[] latest = [new InspectorPreviewTarget { Value = "latest" }];
            panel.InspectedObjects = latest;

            Assert.That(oldEditor.IsDestroyed, Is.True);
            Assert.That(ProviderTargets(panel), Is.SameAs(latest));
        }
        finally
        {
            oldEditor.Destroying -= veto;
            root.Destroy(true);
        }
    }

    [Test]
    public void DetailFactory_TargetGetterSelectsNewerTarget_RetiresOldHost()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        try
        {
            Assert.That(InvokeResult(panel, "HideDetailEditor"), Is.True);
            UIBoundableTransform detailTransform = Field<UIBoundableTransform>(panel, "_detailTransform");
            int originalChildren = detailTransform.SceneNode!.Transform.ChildCount;
            InspectorPreviewTarget older = (InspectorPreviewTarget)panel.InspectedObjects![0]!;
            object?[] newer = [new InspectorPreviewTarget { Value = "new" }];
            bool selected = false;
            SceneNode? canceledHost = null;
            older.OnRead = () =>
            {
                if (selected) return;
                selected = true;
                canceledHost = CurrentConstructionHost(panel);
                panel.InspectedObjects = newer;
            };

            Invoke(panel, "ShowDetailEditor", typeof(InspectorPreviewTarget).GetProperty(
                nameof(InspectorPreviewTarget.Value))!);

            Assert.That(selected, Is.True);
            Assert.That(canceledHost, Is.Not.Null);
            Assert.That(canceledHost!.IsDestroyed, Is.True);
            Assert.That(ProviderTargets(panel), Is.SameAs(newer));
            Assert.That(detailTransform.SceneNode!.Transform.ChildCount, Is.EqualTo(originalChildren));
            Assert.That(Field<SceneNode?>(panel, "_detailEditor"), Is.Null);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DetailFactory_RootAttachmentCallbackRetiresCanceledHost(bool throwFromCallback)
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        SceneNode? constructing = null;
        XRPropertyChangedEventHandler? changed = null;
        try
        {
            Assert.That(InvokeResult(panel, "HideDetailEditor"), Is.True);
            UIBoundableTransform detailTransform = Field<UIBoundableTransform>(panel, "_detailTransform");
            InspectorPreviewTarget older = (InspectorPreviewTarget)panel.InspectedObjects![0]!;
            object?[] newer = [new InspectorPreviewTarget { Value = "new" }];
            older.OnRead = () =>
            {
                SceneNode captured = CurrentConstructionHost(panel);
                constructing = captured;
                changed = (_, change) =>
                {
                    if (change.PropertyName != nameof(TransformBase.Parent) ||
                        !ReferenceEquals(captured.Transform.Parent, detailTransform)) return;
                    captured.Transform.PropertyChanged -= changed;
                    if (throwFromCallback)
                        throw new InvalidOperationException("Attachment callback failed.");
                    panel.InspectedObjects = newer;
                };
                captured.Transform.PropertyChanged += changed;
            };

            if (throwFromCallback)
                Assert.Throws<TargetInvocationException>(() => Invoke(panel, "ShowDetailEditor",
                    typeof(InspectorPreviewTarget).GetProperty(nameof(InspectorPreviewTarget.Value))!));
            else
                Invoke(panel, "ShowDetailEditor", typeof(InspectorPreviewTarget).GetProperty(
                    nameof(InspectorPreviewTarget.Value))!);

            Assert.That(constructing, Is.Not.Null);
            Assert.That(constructing!.IsDestroyed, Is.True);
            Assert.That(Field<SceneNode?>(panel, "_detailEditor"), Is.Null);
            if (!throwFromCallback)
                Assert.That(ProviderTargets(panel), Is.SameAs(newer));
        }
        finally
        {
            if (constructing is not null && changed is not null)
                constructing.Transform.PropertyChanged -= changed;
            root.Destroy(true);
        }
    }

    [Test]
    public void DetailFactory_RootAttachmentForeignTransferPreservesNewOwner()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        SceneNode foreign = root.NewChild("Foreign detail owner");
        SceneNode? constructing = null;
        XRPropertyChangedEventHandler? changed = null;
        try
        {
            Assert.That(InvokeResult(panel, "HideDetailEditor"), Is.True);
            UIBoundableTransform detailTransform = Field<UIBoundableTransform>(panel, "_detailTransform");
            InspectorPreviewTarget older = (InspectorPreviewTarget)panel.InspectedObjects![0]!;
            older.OnRead = () =>
            {
                SceneNode captured = CurrentConstructionHost(panel);
                constructing = captured;
                changed = (_, change) =>
                {
                    if (change.PropertyName != nameof(TransformBase.Parent) ||
                        !ReferenceEquals(captured.Transform.Parent, detailTransform)) return;
                    captured.Transform.PropertyChanged -= changed;
                    captured.Transform.SetParent(foreign.Transform, false,
                        EParentAssignmentMode.Immediate);
                };
                captured.Transform.PropertyChanged += changed;
            };

            Assert.Throws<TargetInvocationException>(() => Invoke(panel, "ShowDetailEditor",
                typeof(InspectorPreviewTarget).GetProperty(nameof(InspectorPreviewTarget.Value))!));

            Assert.That(constructing, Is.Not.Null);
            Assert.That(constructing!.IsDestroyed, Is.False);
            Assert.That(constructing.Transform.Parent, Is.SameAs(foreign.Transform));
            Assert.That(Field<SceneNode?>(panel, "_detailEditor"), Is.Null);
            Assert.That(Field<System.Collections.IList>(panel, "_retainedDetailConstructions").Count,
                Is.GreaterThan(0));
        }
        finally
        {
            if (constructing is not null && changed is not null)
                constructing.Transform.PropertyChanged -= changed;
            root.Destroy(true);
        }
    }

    [Test]
    public void DetailFactory_GetterReplacesTextTransform_PreservesForeignTransform()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        SceneNode? constructing = null;
        SceneNode? textNode = null;
        UIBoundableTransform? replacement = null;
        try
        {
            Assert.That(InvokeResult(panel, "HideDetailEditor"), Is.True);
            UIBoundableTransform detailTransform = Field<UIBoundableTransform>(panel, "_detailTransform");
            InspectorPreviewTarget older = (InspectorPreviewTarget)panel.InspectedObjects![0]!;
            older.OnRead = () =>
            {
                object active = CurrentConstructionAttempt(panel);
                constructing = (SceneNode)active.GetType().GetProperty("Host",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active)!;
                textNode = (SceneNode)active.GetType().GetProperty("TextNode",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active)!;
                replacement = textNode.SetTransform<UIBoundableTransform>();
            };

            Invoke(panel, "ShowDetailEditor", typeof(InspectorPreviewTarget).GetProperty(
                nameof(InspectorPreviewTarget.Value))!);

            Assert.That(constructing, Is.Not.Null);
            Assert.That(textNode, Is.Not.Null);
            Assert.That(replacement, Is.Not.Null);
            Assert.That(textNode!.Transform, Is.SameAs(replacement));
            Assert.That(replacement!.IsDestroyed, Is.False);
            Assert.That(replacement.Parent, Is.Null);
            Assert.That(constructing!.Transform.Parent, Is.Null);
            Assert.That(detailTransform.SceneNode!.Transform.Children,
                Does.Not.Contain(constructing.Transform));
            Assert.That(Field<SceneNode?>(panel, "_detailEditor"), Is.Null);
            Assert.That(Field<System.Collections.IList>(panel, "_retainedDetailConstructions").Count,
                Is.GreaterThan(0));
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DetailFactory_GetterChangesDetachedWorld_PreservesForeignLeaf(bool changeHost)
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        SceneNode? host = null;
        SceneNode? textNode = null;
        TestWorldContext foreignWorld = new();
        try
        {
            Assert.That(InvokeResult(panel, "HideDetailEditor"), Is.True);
            InspectorPreviewTarget older = (InspectorPreviewTarget)panel.InspectedObjects![0]!;
            older.OnRead = () =>
            {
                object active = CurrentConstructionAttempt(panel);
                host = (SceneNode)active.GetType().GetProperty("Host",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active)!;
                textNode = (SceneNode)active.GetType().GetProperty("TextNode",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active)!;
                (changeHost ? host : textNode)!.SetWorldContext(foreignWorld);
            };

            Invoke(panel, "ShowDetailEditor", typeof(InspectorPreviewTarget).GetProperty(
                nameof(InspectorPreviewTarget.Value))!);

            Assert.That(host, Is.Not.Null);
            Assert.That(textNode, Is.Not.Null);
            Assert.That((changeHost ? host : textNode)!.World, Is.SameAs(foreignWorld));
            Assert.That(host!.Transform.Parent, Is.Null);
            Assert.That(Field<SceneNode?>(panel, "_detailEditor"), Is.Null);
            Assert.That(Field<System.Collections.IList>(panel, "_retainedDetailConstructions").Count,
                Is.GreaterThan(0));
        }
        finally
        {
            textNode?.Destroy(true);
            host?.Destroy(true);
            root.Destroy(true);
        }
    }

    [Test]
    public void DetailFactory_GetterAdoptsAndReleasesText_PreservesOriginalTransfer()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        SceneNode foreign = root.NewChild("Foreign text owner");
        foreign.SetTransform<UIBoundableTransform>();
        SceneNode? host = null;
        SceneNode? textNode = null;
        try
        {
            Assert.That(InvokeResult(panel, "HideDetailEditor"), Is.True);
            InspectorPreviewTarget older = (InspectorPreviewTarget)panel.InspectedObjects![0]!;
            older.OnRead = () =>
            {
                object active = CurrentConstructionAttempt(panel);
                host = (SceneNode)active.GetType().GetProperty("Host",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active)!;
                textNode = (SceneNode)active.GetType().GetProperty("TextNode",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active)!;
                textNode!.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
                textNode.Transform.SetParent(null, false, EParentAssignmentMode.Immediate);
            };

            Invoke(panel, "ShowDetailEditor", typeof(InspectorPreviewTarget).GetProperty(
                nameof(InspectorPreviewTarget.Value))!);

            Assert.That(textNode, Is.Not.Null);
            Assert.That(textNode!.IsDestroyed, Is.False);
            Assert.That(textNode.Transform.Parent, Is.Null);
            Assert.That(host!.Transform.Parent, Is.Null);
            Assert.That(Field<SceneNode?>(panel, "_detailEditor"), Is.Null);
            Assert.That(Field<System.Collections.IList>(panel, "_retainedDetailConstructions").Count,
                Is.GreaterThan(0));
        }
        finally
        {
            textNode?.Destroy(true);
            host?.Destroy(true);
            root.Destroy(true);
        }
    }

    [Test]
    public void DetailFactory_GetterDestroysHost_RetiresDetachedText()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        SceneNode? host = null;
        SceneNode? textNode = null;
        try
        {
            Assert.That(InvokeResult(panel, "HideDetailEditor"), Is.True);
            InspectorPreviewTarget older = (InspectorPreviewTarget)panel.InspectedObjects![0]!;
            older.OnRead = () =>
            {
                object active = CurrentConstructionAttempt(panel);
                host = (SceneNode)active.GetType().GetProperty("Host",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active)!;
                textNode = (SceneNode)active.GetType().GetProperty("TextNode",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active)!;
                host.Destroy(true);
            };

            Invoke(panel, "ShowDetailEditor", typeof(InspectorPreviewTarget).GetProperty(
                nameof(InspectorPreviewTarget.Value))!);

            Assert.That(host, Is.Not.Null);
            Assert.That(textNode, Is.Not.Null);
            Assert.That(host!.IsDestroyed, Is.True);
            Assert.That(textNode!.IsDestroyed, Is.True);
            Assert.That(Field<SceneNode?>(panel, "_detailEditor"), Is.Null);
            Assert.That(Field<System.Collections.IList>(panel, "_retainedDetailConstructions").Count,
                Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DetailFactory_ActivationFailureClearsOnlyItsOwnHost(bool replaceOwner)
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        SceneNode? host = null;
        SceneNode? replacement = null;
        XRPropertyChangedEventHandler? changed = null;
        try
        {
            Assert.That(InvokeResult(panel, "HideDetailEditor"), Is.True);
            UIBoundableTransform detailTransform = Field<UIBoundableTransform>(panel, "_detailTransform");
            InspectorPreviewTarget older = (InspectorPreviewTarget)panel.InspectedObjects![0]!;
            older.OnRead = () =>
            {
                SceneNode captured = CurrentConstructionHost(panel);
                host = captured;
                changed = (_, change) =>
                {
                    if (change.PropertyName != nameof(SceneNode.IsActiveSelf) ||
                        !captured.IsActiveSelf) return;
                    captured.PropertyChanged -= changed;
                    if (replaceOwner)
                    {
                        replacement = detailTransform.SceneNode!.NewChild();
                        SetField(panel, "_detailEditor", replacement);
                    }
                    throw new InvalidOperationException("Activation callback failed.");
                };
                captured.PropertyChanged += changed;
            };

            Assert.Throws<TargetInvocationException>(() => Invoke(panel, "ShowDetailEditor",
                typeof(InspectorPreviewTarget).GetProperty(nameof(InspectorPreviewTarget.Value))!));

            Assert.That(host, Is.Not.Null);
            Assert.That(host!.IsDestroyed, Is.True);
            Assert.That(Field<SceneNode?>(panel, "_detailEditor"),
                replaceOwner ? Is.SameAs(replacement) : Is.Null);
            if (replaceOwner)
                Assert.That(replacement!.IsDestroyed, Is.False);
        }
        finally
        {
            if (host is not null && changed is not null)
                host.PropertyChanged -= changed;
            root.Destroy(true);
        }
    }

    [Test]
    public void DetailFactory_ActivationFailureLeavesPendingHostWithNewerRequest()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, input, _) = CreateFocusedDetail();
        SceneNode? host = null;
        XRPropertyChangedEventHandler? changed = null;
        object?[] newer = [new InspectorPreviewTarget { Value = "newer" }];
        try
        {
            Assert.That(InvokeResult(panel, "HideDetailEditor"), Is.True);
            InspectorPreviewTarget older = (InspectorPreviewTarget)panel.InspectedObjects![0]!;
            older.OnRead = () =>
            {
                SceneNode captured = CurrentConstructionHost(panel);
                host = captured;
                changed = (_, change) =>
                {
                    if (change.PropertyName != nameof(SceneNode.IsActiveSelf) ||
                        !captured.IsActiveSelf) return;
                    captured.PropertyChanged -= changed;
                    input.HasPendingFocusRequest = true;
                    panel.InspectedObjects = newer;
                    throw new InvalidOperationException("Activation callback failed after target change.");
                };
                captured.PropertyChanged += changed;
            };

            Assert.Throws<TargetInvocationException>(() => Invoke(panel, "ShowDetailEditor",
                typeof(InspectorPreviewTarget).GetProperty(nameof(InspectorPreviewTarget.Value))!));

            Assert.That(host, Is.Not.Null);
            Assert.That(host!.IsDestroyed, Is.False);
            Assert.That(Field<SceneNode?>(panel, "_detailEditor"), Is.SameAs(host));
            Assert.That(ProviderTargets(panel), Is.SameAs(newer));
            Assert.That(Field<bool>(panel, "_targetRefreshPending"), Is.True);
            Assert.That(Field<System.Collections.IList>(panel, "_retainedDetailConstructions").Count,
                Is.Zero);

            input.HasPendingFocusRequest = false;
            Invoke(panel, "RetryDeferredTargetRefresh");

            Assert.That(host.IsDestroyed, Is.True);
            Assert.That(Field<SceneNode?>(panel, "_detailEditor"), Is.Null);
            Assert.That(ProviderTargets(panel), Is.SameAs(newer));
            Invoke(panel, "ShowDetailEditor",
                typeof(InspectorPreviewTarget).GetProperty(nameof(InspectorPreviewTarget.Value))!);
            Assert.That(Field<SceneNode?>(panel, "_detailEditor"), Is.Not.Null.And.Not.SameAs(host));
        }
        finally
        {
            input.HasPendingFocusRequest = false;
            if (host is not null && changed is not null)
                host.PropertyChanged -= changed;
            root.Destroy(true);
        }
    }

    [Test]
    public void DetailFactory_NonStringChildrenPublishAndCloseThroughPanel()
    {
        using UITestShaderScope shaders = new();
        var (root, panel, _, _) = CreateFocusedDetail();
        try
        {
            InspectorFloatCurveTestOwner target = new()
            {
                Curve = new XREngine.Animation.PropAnimFloat(2, false, true)
            };
            panel.InspectedObjects = [target];

            Invoke(panel, "ShowDetailEditor", typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!);

            SceneNode detail = Field<SceneNode>(panel, "_detailEditor");
            Assert.That(detail, Is.Not.Null);
            Assert.That(detail.IsDestroyed, Is.False);
            Assert.That(detail.Transform.ChildCount, Is.GreaterThan(0));
            Assert.That(detail.Transform.Parent,
                Is.SameAs(Field<UIBoundableTransform>(panel, "_detailTransform")));
            Assert.That(Field<System.Collections.IList>(panel, "_retainedDetailConstructions").Count,
                Is.Zero);
            Assert.That(InvokeResult(panel, "HideDetailEditor"), Is.True);
            Assert.That(detail.IsDestroyed, Is.True);
        }
        finally { root.Destroy(true); }
    }

    private static SceneNode CurrentConstructionHost(InspectorPanel panel)
    {
        object active = CurrentConstructionAttempt(panel);
        return (SceneNode)active.GetType().GetProperty("Host",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active)!;
    }

    private static object CurrentConstructionAttempt(InspectorPanel panel)
    {
        var attempts = Field<System.Collections.IList>(panel, "_activeDetailConstructions");
        return attempts[attempts.Count - 1]!;
    }

    private static object?[]? ProviderTargets(InspectorPanel panel)
    {
        object provider = Field<object>(panel, "_virtualPropertyProvider");
        return (object?[]?)provider.GetType().GetProperty("Targets")!.GetValue(provider);
    }

    private static PropertyInfo OtherProperty()
        => typeof(InspectorPreviewTarget).GetProperty(nameof(InspectorPreviewTarget.OnRead))!;

    private static (SceneNode Root, InspectorPanel Panel, InspectorFocusTestInput Input,
        UITextInputComponent Control) CreateFocusedDetail()
    {
        SceneNode root = new("Inspector focus", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        InspectorFocusTestInput input = root.AddComponent<InspectorFocusTestInput>()!;
        SceneNode owner = new(root, "Inspector", new UIBoundableTransform());
        InspectorPanel panel = owner.AddComponent<InspectorPanel>()!;
        panel.IsLocked = true;
        panel.InspectedObjects = [new InspectorPreviewTarget { Value = "draft" }];
        root.IsActiveSelf = true;
        Invoke(panel, "ShowDetailEditor", typeof(InspectorPreviewTarget).GetProperty(
            nameof(InspectorPreviewTarget.Value))!);
        SceneNode detail = Field<SceneNode>(panel, "_detailEditor");
        UITextInputComponent control = Descendants(detail)
            .Select(node => node.GetComponent<UITextInputComponent>()).OfType<UITextInputComponent>().Single();
        input.FocusedComponent = control;
        return (root, panel, input, control);
    }

    private static void CaptureAndClose(InspectorPanel panel)
    {
        Invoke(panel, "CaptureViewState");
        Invoke(panel, "HideDetailEditor");
    }

    private static IEnumerable<SceneNode> Descendants(SceneNode root)
    {
        Stack<SceneNode> pending = new();
        pending.Push(root);
        while (pending.TryPop(out SceneNode? node))
        {
            yield return node;
            foreach (var child in node.Transform.Children)
                if (child.SceneNode is { } childNode)
                    pending.Push(childNode);
        }
    }

    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void SetField(object owner, string name, object? value)
        => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    private static void Invoke(InspectorPanel panel, string name, params object?[] args)
        => typeof(InspectorPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, args);

    private static object? InvokeResult(InspectorPanel panel, string name, params object?[] args)
        => typeof(InspectorPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, args);
}
