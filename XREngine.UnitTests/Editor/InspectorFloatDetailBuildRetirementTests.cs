using System.Reflection;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class InspectorFloatDetailBuildRetirementTests
{
    [Test]
    public void CurveDetailRetiredWhenFirstCardIsAttachedDoesNotPublishControls()
    {
        using UITestShaderScope shaders = new();
        InspectorFloatCurveTestOwner owner = new()
        {
            Curve = new PropAnimFloat(2, false, true)
        };
        PropertyInfo property = typeof(InspectorFloatCurveTestOwner).GetProperty(
            nameof(InspectorFloatCurveTestOwner.Curve))!;
        CheckRetirementDuringFirstChild(property, [owner],
            InspectorPropertyEditors.EngineTypes.CreatePropAnimFloatEditor);
    }

    [Test]
    public void StateMachineNavigatorRetiredWhenFirstCardIsAttachedDoesNotPublishRows()
    {
        using UITestShaderScope shaders = new();
        AnimStateMachine machine = new();
        PropertyInfo property = typeof(AnimStateMachine).GetProperty(
            nameof(AnimStateMachine.Layers))!;
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(
            property.PropertyType, out var create), Is.True);
        CheckRetirementDuringFirstChild(property, [machine], create!);
    }

    [Test]
    public void CurveDetailRetiredWhenTickIsAddedNeverCreatesACard()
    {
        using UITestShaderScope shaders = new();
        InspectorFloatCurveTestOwner owner = new()
        {
            Curve = new PropAnimFloat(2, false, true)
        };
        PropertyInfo property = typeof(InspectorFloatCurveTestOwner).GetProperty(
            nameof(InspectorFloatCurveTestOwner.Curve))!;
        SceneNode node = new("Retiring curve detail") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        bool retiredDuringTickCreation = false;
        node.ComponentAdded += pair =>
        {
            if (pair.comp is not InspectorFloatCurveTickComponent) return;
            retiredDuringTickCreation = true;
            node.GetComponent<InspectorListDetailRefreshComponent>()!.Retire();
        };
        try
        {
            InspectorPropertyEditors.EngineTypes.CreatePropAnimFloatEditor(
                node, property, [owner]);
            Assert.That(retiredDuringTickCreation, Is.True);
            Assert.That(node.Transform.Children, Is.Empty);
            Assert.That((object?)node.GetComponent<InspectorListDetailRefreshComponent>()!.Refresh,
                Is.Null);
            Assert.That((object?)node.GetComponent<InspectorFloatCurveTickComponent>()!.Refresh,
                Is.Null);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void CurveDetailRetiredByListSettingDoesNotPublishProvider()
    {
        using UITestShaderScope shaders = new();
        InspectorFloatCurveTestOwner owner = new()
        {
            Curve = new PropAnimFloat(2, false, true)
        };
        PropertyInfo property = typeof(InspectorFloatCurveTestOwner).GetProperty(
            nameof(InspectorFloatCurveTestOwner.Curve))!;
        SceneNode node = new("Retiring list detail") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        UIListTransform? list = null;
        HashSet<object> watchedTransforms = [];
        void ObserveChildren(SceneNode current)
        {
            if (!watchedTransforms.Add(current.Transform)) return;
            current.Transform.Children.PostAnythingAdded += child =>
            {
                if (child.SceneNode is { } added) Observe(added);
            };
        }
        void Observe(SceneNode current)
        {
            current.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(SceneNode.Transform)) return;
                ObserveChildren(current);
                if (current.Transform is not UIListTransform candidate) return;
                candidate.PropertyChanging += (_, setting) =>
                {
                    if (setting.PropertyName != nameof(UIListTransform.ItemSize) ||
                        candidate.Parent is not UIScrollableTransform)
                        return;
                    list = candidate;
                    node.GetComponent<InspectorListDetailRefreshComponent>()!.Retire();
                };
            };
            ObserveChildren(current);
        }
        Observe(node);
        try
        {
            InspectorPropertyEditors.EngineTypes.CreatePropAnimFloatEditor(
                node, property, [owner]);
            Assert.That(list, Is.Not.Null);
            Assert.That(list!.VirtualProvider, Is.Null);
            Assert.That((object?)node.GetComponent<InspectorListDetailRefreshComponent>()!.Refresh,
                Is.Null);
            Assert.That((object?)node.GetComponent<InspectorFloatCurveTickComponent>()!.Refresh,
                Is.Null);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void CurveDetailRetiredByInputLabelDoesNotBuildRemainingControls()
    {
        using UITestShaderScope shaders = new();
        InspectorFloatCurveTestOwner owner = new()
        {
            Curve = new PropAnimFloat(2, false, true)
        };
        PropertyInfo property = typeof(InspectorFloatCurveTestOwner).GetProperty(
            nameof(InspectorFloatCurveTestOwner.Curve))!;
        SceneNode node = new("Retiring input detail") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        UITextInputComponent? input = null;
        HashSet<object> watchedTransforms = [];
        void ObserveChildren(SceneNode current)
        {
            if (!watchedTransforms.Add(current.Transform)) return;
            current.Transform.Children.PostAnythingAdded += child =>
            {
                if (child.SceneNode is { } added) Observe(added);
            };
        }
        void Observe(SceneNode current)
        {
            current.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(SceneNode.Transform))
                    ObserveChildren(current);
            };
            current.ComponentAdded += pair =>
            {
                if (pair.comp is not UITextInputComponent candidate) return;
                candidate.PropertyChanging += (_, change) =>
                {
                    if (change.PropertyName != nameof(candidate.AccessibilityLabel) ||
                        input is not null)
                        return;
                    input = candidate;
                    node.GetComponent<InspectorListDetailRefreshComponent>()!.Retire();
                };
            };
            ObserveChildren(current);
        }
        Observe(node);
        try
        {
            InspectorPropertyEditors.EngineTypes.CreatePropAnimFloatEditor(
                node, property, [owner]);
            Assert.That(input, Is.Not.Null);
            SceneNode card = node.Transform.Children.Single().SceneNode!;
            Assert.That(card.Transform.Children.Count, Is.EqualTo(3),
                "Retirement during the first input must stop before actions and plot creation.");
            Assert.That((object?)node.GetComponent<InspectorListDetailRefreshComponent>()!.Refresh,
                Is.Null);
            Assert.That((object?)node.GetComponent<InspectorFloatCurveTickComponent>()!.Refresh,
                Is.Null);
        }
        finally { node.Destroy(true); }
    }

    private static void CheckRetirementDuringFirstChild(PropertyInfo property,
        object?[] owners, Action<SceneNode, PropertyInfo, object?[]?> create)
    {
        SceneNode node = new("Retiring inspector detail") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        bool retiredDuringAttachment = false;
        HashSet<object> watchedTransforms = [];
        void ObserveCurrentChildren()
        {
            if (!watchedTransforms.Add(node.Transform)) return;
            node.Transform.Children.PostAnythingAdded += _ =>
            {
                if (retiredDuringAttachment) return;
                retiredDuringAttachment = true;
                node.GetComponent<InspectorListDetailRefreshComponent>()!.Retire();
            };
        }
        node.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(SceneNode.Transform))
                ObserveCurrentChildren();
        };
        ObserveCurrentChildren();
        try
        {
            create(node, property, owners);
            Assert.That(retiredDuringAttachment, Is.True);
            Assert.That((object?)node.GetComponent<InspectorListDetailRefreshComponent>()!.Refresh,
                Is.Null);
            Assert.That((object?)node.GetComponent<InspectorFloatCurveTickComponent>()!.Refresh,
                Is.Null);
            SceneNode card = node.Transform.Children.Single().SceneNode!;
            Assert.That(card.Transform.Children, Is.Empty,
                "A retired initial build must not attach fields, rows, or a plot.");
        }
        finally { node.Destroy(true); }
    }
}
