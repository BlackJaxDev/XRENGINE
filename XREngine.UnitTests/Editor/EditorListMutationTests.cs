using System.Collections;
using System.Reflection;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;
using XREngine.Scene.Transforms;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class EditorListMutationTests
{
    [SetUp]
    public void Clear() => Undo.ClearHistory();

    [TearDown]
    public void ClearAfter() => Undo.ClearHistory();

    [Test]
    public void MultiOwnerSetUsesOneUndoStepAndKeepsEachOldValue()
    {
        EditorListMutationTestTarget first = new() { Values = [1, 2] };
        EditorListMutationTestTarget second = new() { Values = [5, 6] };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(first.Values))!;
        EditorListMutation.Apply([(first, property), (second, property)],
            EEditorListMutation.Set, 1, _ => 9);
        Assert.Multiple(() =>
        {
            Assert.That(first.Values, Is.EqualTo(new[] { 1, 9 }));
            Assert.That(second.Values, Is.EqualTo(new[] { 5, 9 }));
        });
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(first.Values, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(second.Values, Is.EqualTo(new[] { 5, 6 }));
            Assert.That(Undo.CanUndo, Is.False);
        });
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(first.Values[1], Is.EqualTo(9));
        Assert.That(second.Values[1], Is.EqualTo(9));
    }

    [Test]
    public void InsertRemoveAndNestedHistoryRestoreExactContents()
    {
        EditorListMutationTestTarget target = new() { Values = [1, 2] };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.Values))!;
        var owners = new (object, System.Reflection.PropertyInfo)[] { (target, property) };
        EditorListMutation.Apply(owners, EEditorListMutation.Insert, 1, _ => 7);
        EditorListMutation.Apply(owners, EEditorListMutation.Remove, 2, null);
        Assert.That(target.Values, Is.EqualTo(new[] { 1, 7 }));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values, Is.EqualTo(new[] { 1, 7, 2 }));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(target.Values, Is.EqualTo(new[] { 1, 7 }));
    }

    [Test]
    public void ForeignChangeBlocksReplayWithoutChangingList()
    {
        EditorListMutationTestTarget target = new() { Values = [1, 2] };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.Values))!;
        EditorListMutation.Apply([(target, property)], EEditorListMutation.Set, 1, _ => 8);
        target.Values[0] = 42;
        Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        Assert.That(target.Values, Is.EqualTo(new[] { 42, 8 }));
        Assert.That(Undo.CanUndo, Is.True);
    }

    [Test]
    public void ReplacedCollectionBlocksReplayWithoutTouchingReplacement()
    {
        EditorListMutationTestTarget target = new() { Values = [1, 2] };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.Values))!;
        EditorListMutation.Apply([(target, property)], EEditorListMutation.Set, 0, _ => 6);
        target.Values = [7, 8];
        Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        Assert.That(target.Values, Is.EqualTo(new[] { 7, 8 }));
        Assert.That(Undo.CanUndo, Is.True);
    }

    [Test]
    public void StaleAddHeaderRejectsReplacementAndExternalAppend()
    {
        EditorListMutationTestTarget target = new() { Values = [] };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.Values))!;
        EditorListMutationContext context = new();
        context.Rows.Reset(0);
        EditorListSourceLease source = EditorListSourceLease.Capture(target, property);
        target.Values = [7];
        Assert.Throws<InvalidOperationException>(() => EditorListMutation.Apply(
            [(target, property)], EEditorListMutation.Insert, -1, _ => 3,
            context, expectedSources: [source]));
        Assert.That(target.Values, Is.EqualTo(new[] { 7 }));
        source = EditorListSourceLease.Capture(target, property);
        target.Values.Add(8);
        Assert.Throws<InvalidOperationException>(() => EditorListMutation.Apply(
            [(target, property)], EEditorListMutation.Insert, -1, _ => 3,
            context, expectedSources: [source]));
        Assert.That(target.Values, Is.EqualTo(new[] { 7, 8 }));
    }

    [Test]
    public void ValueFactoryCannotAdoptAnotherTargetsReplacement()
    {
        EditorListMutationTestTarget first = new() { Values = [1] };
        EditorListMutationTestTarget second = new() { Values = [2] };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(first.Values))!;
        Assert.Throws<InvalidOperationException>(() => EditorListMutation.Apply(
            [(first, property), (second, property)], EEditorListMutation.Set, 0, old =>
            {
                if ((int)old! == 1)
                    second.Values = [8];
                return 9;
            }));
        Assert.That(first.Values, Is.EqualTo(new[] { 1 }));
        Assert.That(second.Values, Is.EqualTo(new[] { 8 }));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CallbackGetterIsRejectedBeforeChangingAnyOwner(bool remove)
    {
        EEditorListMutation operation = remove ? EEditorListMutation.Remove : EEditorListMutation.Set;
        EditorListMutationTestTarget first = new() { Values = [1, 2] };
        EditorListMutationGetterTarget second = new() { Values = [3, 4] };
        var firstProperty = typeof(EditorListMutationTestTarget).GetProperty(nameof(first.Values))!;
        var secondProperty = typeof(EditorListMutationGetterTarget).GetProperty(nameof(second.Values))!;
        int getterCalls = 0;
        second.OnGet = () =>
        {
            getterCalls++;
            first.Values = [8, 2];
        };
        Assert.Throws<InvalidOperationException>(() => EditorListMutation.Apply(
            [(first, firstProperty), (second, secondProperty)], operation, 1,
            operation == EEditorListMutation.Set ? _ => 9 : null));
        Assert.That(getterCalls, Is.Zero);
        Assert.That(first.Values, Is.EqualTo(new[] { 1, 2 }));
        second.OnGet = null;
        Assert.That(second.Values, Is.EqualTo(new[] { 3, 4 }));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void VirtualBasePropertyRejectsHiddenGetterDispatch()
    {
        EditorListMutationTestTarget first = new() { Values = [1, 2] };
        EditorListVirtualOverrideTarget second = new();
        var firstProperty = typeof(EditorListMutationTestTarget).GetProperty(nameof(first.Values))!;
        var baseProperty = typeof(EditorListVirtualBaseTarget).GetProperty(nameof(second.Values))!;
        int getterCalls = 0;
        second.OnGet = () => getterCalls++;
        Assert.That(EditorListMutation.SupportsSource(baseProperty), Is.False);
        Assert.Throws<InvalidOperationException>(() => EditorListMutation.Apply(
            [(first, firstProperty), (second, baseProperty)], EEditorListMutation.Set, 0, _ => 9));
        Assert.That(getterCalls, Is.Zero);
        Assert.That(first.Values, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void AutoPropertyReplacementDuringPreparationCannotBecomeEditSource()
    {
        EditorListMutationTestTarget target = new() { Values = [1, 2] };
        List<int> oldList = target.Values;
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.Values))!;
        Assert.Throws<InvalidOperationException>(() => EditorListMutation.Apply(
            [(target, property)], EEditorListMutation.Set, 0, _ =>
            {
                target.Values = [7, 8];
                return 9;
            }));
        Assert.That(oldList, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(target.Values, Is.EqualTo(new[] { 7, 8 }));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void RetireDuringOldDetailChildDeactivationStopsRebuild()
    {
        EditorListMutationTestTarget target = new() { Values = [1, 2] };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.Values))!;
        SceneNode sceneRoot = new("List detail world");
        XRWorld authored = new("List detail world", new XRScene("Detail", sceneRoot));
        using RuntimeWorld world = new(new JitterScene(), authored);
        SceneNode detail = new(sceneRoot, "List detail");
        detail.SetTransform<UIBoundableTransform>();
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(
            typeof(List<int>), out var create), Is.True);
        create!(detail, property, [target]);
        InspectorListDetailRefreshComponent lifetime = detail.Components
            .OfType<InspectorListDetailRefreshComponent>().Single();
        SceneNode container = detail.Transform.Children.Single().SceneNode!;
        int oldChildCount = container.Transform.Children.Count;
        SceneNode oldChild = container.Transform.Children.First().SceneNode!;
        EditorListRetireOnDeactivateComponent probe =
            oldChild.AddComponent<EditorListRetireOnDeactivateComponent>()!;
        probe.OnDeactivate = lifetime.Retire;
        target.Values = [3, 4];
        lifetime.Refresh!();
        Assert.That(probe.Deactivated, Is.True);
        Assert.That(lifetime.Refresh is null, Is.True);
        Assert.That(container.Transform.Children.Count, Is.EqualTo(oldChildCount));
        Assert.That(oldChild.IsActiveSelf, Is.False);
        sceneRoot.Destroy(true);
    }

    [TestCase(nameof(EditorCollectionGetterTarget.Dictionary))]
    [TestCase(nameof(EditorCollectionGetterTarget.Linked))]
    [TestCase(nameof(EditorCollectionGetterTarget.Generic))]
    public void RetireDuringCollectionSourceProbeStopsBranchBuild(string propertyName)
    {
        EditorCollectionGetterTarget target = new();
        var property = typeof(EditorCollectionGetterTarget).GetProperty(propertyName)!;
        SceneNode detail = new("Collection detail");
        detail.SetTransform<UIBoundableTransform>();
        int getterCalls = 0;
        target.OnGet = () =>
        {
            getterCalls++;
            if (getterCalls == 1)
                detail.Components.OfType<InspectorListDetailRefreshComponent>().Single().Retire();
        };
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(
            property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        InspectorListDetailRefreshComponent lifetime = detail.Components
            .OfType<InspectorListDetailRefreshComponent>().Single();
        SceneNode container = detail.Transform.Children.Single().SceneNode!;
        Assert.That(getterCalls, Is.EqualTo(1));
        Assert.That(lifetime.Refresh is null, Is.True);
        Assert.That(container.Transform.Children, Is.Empty);
        detail.Destroy(true);
    }

    [TestCase(nameof(EditorCollectionToStringTarget.DictionaryKey))]
    [TestCase(nameof(EditorCollectionToStringTarget.DictionaryValue))]
    [TestCase(nameof(EditorCollectionToStringTarget.GenericValue))]
    public void ThrowAfterRetirementLeavesNoPublishedCollectionBranch(string propertyName)
    {
        EditorCollectionToStringTarget target = new();
        var property = typeof(EditorCollectionToStringTarget).GetProperty(propertyName)!;
        SceneNode detail = new("Collection detail");
        detail.SetTransform<UIBoundableTransform>();
        int textCalls = 0;
        target.OnToString = () =>
        {
            textCalls++;
            detail.Components.OfType<InspectorListDetailRefreshComponent>().Single().Retire();
            throw new InvalidOperationException("Injected row text failure.");
        };
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(
            property.PropertyType, out var create), Is.True);
        Assert.That(() => create!(detail, property, [target]), Throws.Exception);
        SceneNode container = detail.Transform.Children.Single().SceneNode!;
        Assert.That(textCalls, Is.EqualTo(1));
        Assert.That(container.Transform.Children.All(child => child.SceneNode?.IsActiveSelf == false), Is.True);
        detail.Destroy(true);
    }

    [TestCase(nameof(EditorCollectionToStringTarget.DictionaryKey))]
    [TestCase(nameof(EditorCollectionToStringTarget.DictionaryValue))]
    [TestCase(nameof(EditorCollectionToStringTarget.GenericValue))]
    public void ReturningAfterRetirementDoesNotPublishIntoAnOldRow(string propertyName)
    {
        EditorCollectionToStringTarget target = new();
        PropertyInfo property = typeof(EditorCollectionToStringTarget).GetProperty(propertyName)!;
        SceneNode detail = new("Collection detail");
        detail.SetTransform<UIBoundableTransform>();
        List<(SceneNode Node, int ChildCount)> capturedNodes = [];
        List<(UITextComponent Text, string? Value)> capturedText = [];
        int textCalls = 0;
        target.OnToString = () =>
        {
            textCalls++;
            foreach (SceneNode node in CollectionDetailNodes(detail))
            {
                capturedNodes.Add((node, node.Transform.Children.Count));
                foreach (UITextComponent text in node.Components.OfType<UITextComponent>())
                    capturedText.Add((text, text.Text));
            }
            detail.Components.OfType<InspectorListDetailRefreshComponent>().Single().Retire();
        };
        Assert.That(InspectorPropertyEditors.CollectionTypes.TryCreateEditor(
            property.PropertyType, out var create), Is.True);
        create!(detail, property, [target]);
        Assert.That(textCalls, Is.EqualTo(1));
        Assert.That(capturedNodes, Is.Not.Empty);
        foreach ((SceneNode node, int childCount) in capturedNodes)
            Assert.That(node.Transform.Children.Count, Is.LessThanOrEqualTo(childCount));
        foreach ((UITextComponent text, string? value) in capturedText)
            Assert.That(text.Text, Is.EqualTo(value));
        detail.Destroy(true);
    }

    private static IEnumerable<SceneNode> CollectionDetailNodes(SceneNode root)
    {
        Stack<SceneNode> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            SceneNode node = pending.Pop();
            yield return node;
            foreach (var child in node.Transform.Children)
                if (child.SceneNode is { } childNode)
                    pending.Push(childNode);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RetiredNonListElementEditorCannotWriteItsOldCollection(bool linked)
    {
        object target = linked ? new EditorCollectionMutationTarget() :
            new EditorDictionaryMutationTestTarget
            {
                Values = new Dictionary<string, int>(StringComparer.Ordinal) { ["one"] = 1 }
            };
        string propertyName = linked ? nameof(EditorCollectionMutationTarget.Linked) :
            nameof(EditorDictionaryMutationTestTarget.Values);
        PropertyInfo property = target.GetType().GetProperty(propertyName)!;
        SceneNode host = new("Retired collection host");
        Type contextType = typeof(InspectorPropertyEditors.CollectionTypes)
            .GetNestedType("CollectionEditorContext", BindingFlags.NonPublic)!;
        object context = Activator.CreateInstance(contextType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null, args: [property, new object?[] { target }, property.PropertyType, host],
            culture: null)!;
        Type bindingType = typeof(InspectorPropertyEditors.CollectionTypes)
            .GetNestedType(linked ? "LinkedListElementBinding`1" : "DictionaryValueBinding`2",
                BindingFlags.NonPublic)!;
        bindingType = linked ? bindingType.MakeGenericType(typeof(int)) :
            bindingType.MakeGenericType(typeof(string), typeof(int));
        Func<bool> isCurrent = () => (bool)contextType.GetMethod("IsCurrentBuild")!
            .Invoke(context, [0UL])!;
        object source = linked ? EditorLinkedMutation.Capture(target, property, isCurrent) :
            EditorDictionaryMutation.Capture(target, property, isCurrent);
        object binding = Activator.CreateInstance(bindingType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null, args: linked
                ? [source, 0, (Action)(() => { })]
                : [source, "one", (Action)(() => { })],
            culture: null)!;
        contextType.GetMethod("Retire")!.Invoke(context, null);
        Assert.Throws<TargetInvocationException>(() =>
            bindingType.GetProperty("Value")!.SetValue(binding, 9));
        int value = linked ? ((EditorCollectionMutationTarget)target).Linked.First!.Value :
            ((EditorDictionaryMutationTestTarget)target).Values["one"];
        Assert.That(value, Is.EqualTo(1));
        host.Destroy(true);
    }

    [Test]
    public void MovingComponentToAnotherSceneBlocksItsOldListUndo()
    {
        SceneNode rootA = new("A"), rootB = new("B");
        SceneNode child = new(rootA, "List owner");
        EditorListSceneTestComponent component = child.AddComponent<EditorListSceneTestComponent>()!;
        XRWorld authored = new("List world", new XRScene("A", rootA), new XRScene("B", rootB));
        using RuntimeWorld world = new(new JitterScene(), authored);
        var property = typeof(EditorListSceneTestComponent).GetProperty(nameof(component.Values))!;
        try
        {
            EditorListMutation.Apply([(component, property)], EEditorListMutation.Set, 0, _ => 9);
            child.Transform.SetParent(rootB.Transform, false, EParentAssignmentMode.Immediate);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(component.Values, Is.EqualTo(new[] { 9, 2 }));
            Assert.That(Undo.CanUndo, Is.True);
        }
        finally { rootA.Destroy(true); rootB.Destroy(true); }
    }

    [Test]
    public void BoundRowNotifiesTheCommittedValueSynchronously()
    {
        EditorListMutationTestTarget target = new() { Values = [2, 3] };
        EditorListMutationContext context = new();
        context.Rows.Reset(2);
        EditorListElementBindingTestDouble binding = new(target, context, 1);
        var property = typeof(EditorListElementBindingTestDouble).GetProperty(nameof(binding.Value))!;
        EditorPropertyAccess access = new(property, [binding]);
        int observed = -1;
        access.Changed += () => observed = binding.Value;
        access.SetValue(9);
        Assert.That(observed, Is.EqualTo(9));
        Assert.That(target.Values, Is.EqualTo(new[] { 2, 9 }));
    }

    [Test]
    public void RetiredOrReplacedRowRejectsOldSetAndRemove()
    {
        EditorListMutationTestTarget target = new() { Values = [1, 2] };
        EditorListMutationContext context = new();
        context.Rows.Reset(2);
        EditorListElementBindingTestDouble binding = new(target, context, 0);
        target.Values = [7, 8];
        Assert.Throws<InvalidOperationException>(() => EditorListMutation.Set([binding], _ => 3));
        Assert.Throws<InvalidOperationException>(() => EditorListMutation.Remove([binding]));
        context.Retire();
        Assert.Throws<InvalidOperationException>(() => EditorListMutation.Set([binding], _ => 3));
        Assert.That(target.Values, Is.EqualTo(new[] { 7, 8 }));
    }

    [Test]
    public void ThrowingHistoryNotificationDoesNotUndoCommittedListEdit()
    {
        EditorListMutationTestTarget target = new() { Values = [1, 2] };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.Values))!;
        Action throwOnHistory = () => throw new InvalidOperationException("Injected history callback failure.");
        Undo.HistoryChanged += throwOnHistory;
        try
        {
            EditorListMutation.Apply([(target, property)], EEditorListMutation.Set, 0, _ => 9);
            Assert.That(target.Values, Is.EqualTo(new[] { 9, 2 }));
            Assert.That(Undo.CanUndo, Is.True);
        }
        finally { Undo.HistoryChanged -= throwOnHistory; }
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void ValueFactoryCannotReenterSameListThroughAnotherOwner()
    {
        List<int> shared = [1, 2];
        EditorListMutationTestTarget first = new() { Values = shared };
        EditorListMutationTestTarget second = new() { Values = shared };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(first.Values))!;
        bool rejected = false;
        EditorListMutation.Apply([(first, property)], EEditorListMutation.Set, 0, _ =>
        {
            try { EditorListMutation.Apply([(second, property)], EEditorListMutation.Set, 1, _ => 7); }
            catch (InvalidOperationException) { rejected = true; }
            return 9;
        });
        Assert.That(rejected, Is.True);
        Assert.That(shared, Is.EqualTo(new[] { 9, 2 }));
    }

    [Test]
    public void ThrowingViewRefreshLeavesReceiptUndoable()
    {
        EditorListMutationTestTarget target = new() { Values = [1, 2] };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.Values))!;
        EditorListMutationContext context = new();
        context.Rows.Reset(2);
        context.RefreshRequested = () => throw new InvalidOperationException("Injected view failure.");
        EditorListMutation.Apply([(target, property)], EEditorListMutation.Set, 1, _ => 5, context);
        Assert.That(target.Values, Is.EqualTo(new[] { 1, 5 }));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void AddPreparationDoesNotConstructReferenceElements()
    {
        Assert.That(EditorListMutation.CreateDefaultElement(typeof(EditorListMutationTestTarget)), Is.Null);
        Assert.That(EditorListMutation.CreateDefaultElement(typeof(string)), Is.EqualTo(string.Empty));
    }

    [Test]
    public void HistoryCallbackCannotReplayBeforeListFinalization()
    {
        EditorListMutationTestTarget target = new() { Values = [1] };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.Values))!;
        bool? replayedEarly = null;
        Action onHistory = () => replayedEarly = Undo.TryUndo();
        Undo.HistoryChanged += onHistory;
        try { EditorListMutation.Apply([(target, property)], EEditorListMutation.Set, 0, _ => 9); }
        finally { Undo.HistoryChanged -= onHistory; }
        Assert.That(replayedEarly, Is.False);
        Assert.That(target.Values[0], Is.EqualTo(9));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values[0], Is.EqualTo(1));
    }

    [Test]
    public void ArraySetWorksAndArrayResizeOrCustomListRejects()
    {
        EditorListMutationTestTarget target = new() { FixedValues = [3, 4] };
        var array = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.FixedValues))!;
        EditorListMutation.Apply([(target, array)], EEditorListMutation.Set, 1, _ => 9);
        Assert.That(target.FixedValues, Is.EqualTo(new[] { 3, 9 }));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.FixedValues, Is.EqualTo(new[] { 3, 4 }));
        Assert.Throws<InvalidOperationException>(() =>
            EditorListMutation.Apply([(target, array)], EEditorListMutation.Insert, 0, _ => 5));
        var custom = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.CustomValues))!;
        Assert.Throws<InvalidOperationException>(() =>
            EditorListMutation.Apply([(target, custom)], EEditorListMutation.Set, 0, _ => 5));
    }

    [Test]
    public void ArrayListInsertAndUndoPreserveExactEntries()
    {
        EditorListMutationTestTarget target = new() { LooseValues = new() { "a", "b" } };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(target.LooseValues))!;
        EditorListMutation.Apply([(target, property)], EEditorListMutation.Insert, 1, _ => "x");
        Assert.That(target.LooseValues.Cast<object?>(), Is.EqualTo(new object?[] { "a", "x", "b" }));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.LooseValues.Cast<object?>(), Is.EqualTo(new object?[] { "a", "b" }));
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(target.LooseValues.Cast<object?>(), Is.EqualTo(new object?[] { "a", "x", "b" }));
    }

    [Test]
    public void InvalidSecondTargetLeavesFirstUnchanged()
    {
        EditorListMutationTestTarget first = new() { Values = [1, 2] };
        EditorListMutationTestTarget second = new() { Values = [3] };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(first.Values))!;
        Assert.Throws<InvalidOperationException>(() => EditorListMutation.Apply(
            [(first, property), (second, property)], EEditorListMutation.Set, 1, _ => 9));
        Assert.That(first.Values, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(second.Values, Is.EqualTo(new[] { 3 }));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void SharedListMutatesOnceAndPreservesOtherOccurrences()
    {
        List<int> shared = [4, 4, 4];
        EditorListMutationTestTarget first = new() { Values = shared };
        EditorListMutationTestTarget second = new() { Values = shared };
        var property = typeof(EditorListMutationTestTarget).GetProperty(nameof(first.Values))!;
        EditorListMutationContext context = new();
        context.Rows.Reset(3);
        ulong firstId = context.Rows.GetItemId(0);
        ulong lastId = context.Rows.GetItemId(2);
        EditorListMutation.Apply([(first, property), (second, property)],
            EEditorListMutation.Remove, 1, null, context);
        Assert.That(shared, Is.EqualTo(new[] { 4, 4 }));
        Assert.That(context.Rows.GetItemId(0), Is.EqualTo(firstId));
        Assert.That(context.Rows.GetItemId(1), Is.EqualTo(lastId));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(shared, Is.EqualTo(new[] { 4, 4, 4 }));
        Assert.That(context.Rows.GetItemId(0), Is.EqualTo(firstId));
        Assert.That(context.Rows.GetItemId(2), Is.EqualTo(lastId));
    }

}
