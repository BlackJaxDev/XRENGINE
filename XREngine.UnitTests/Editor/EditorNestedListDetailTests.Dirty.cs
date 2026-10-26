using System.Collections;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Scene;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

public sealed partial class EditorNestedListDetailTests
{
    [Test]
    public void NestedHistoryPublicationCannotDirtyAReplacedSourceDocument()
    {
        NestedDirtyAsset target = new();
        XRWorld original = new("Original source"), replacement = new("Replacement source");
        target.SourceAsset = original;
        var (paths, context) = CaptureDirtyPaths(target);
        bool once = true;
        void ReplaceSource()
        {
            if (!once) return;
            once = false;
            target.SourceAsset = replacement;
        }
        Undo.HistoryChanged += ReplaceSource;
        try
        {
            Assert.That(EditorNestedListMutation.Apply(paths, EEditorListMutation.Set, 0,
                _ => 42, context, static () => true), Is.True);
            Assert.That(once, Is.False);
            Assert.That(target.Values[0][0], Is.EqualTo(42));
            Assert.That(target.SourceAsset, Is.SameAs(replacement));
            Assert.That(original.IsDirty, Is.False);
            Assert.That(replacement.IsDirty, Is.False);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(target.Values[0][0], Is.EqualTo(42));
        }
        finally
        {
            Undo.HistoryChanged -= ReplaceSource;
            Undo.ClearHistory();
            context.Retire();
            target.SourceAsset = target;
            target.Destroy(true); original.Destroy(true); replacement.Destroy(true);
        }
    }

    [Test]
    public void NestedDirtyCallbacksRecheckEachPeerAndKeepIndependentDocumentsDirty()
    {
        NestedDirtyAsset first = new(), second = new(), third = new();
        XRWorld firstDocument = new("First source"), secondDocument = new("Second source"),
            thirdDocument = new("Third source"), replacement = new("Replacement source");
        first.SourceAsset = firstDocument;
        second.SourceAsset = secondDocument;
        third.SourceAsset = thirdDocument;
        var (paths, context) = CaptureDirtyPaths(first, second, third);
        XRPropertyChangedEventHandler replacePeer = (_, args) =>
        {
            if (args.PropertyName == nameof(XRWorld.IsDirty) && firstDocument.IsDirty)
                second.SourceAsset = replacement;
        };
        firstDocument.PropertyChanged += replacePeer;
        try
        {
            Assert.That(EditorNestedListMutation.Apply(paths, EEditorListMutation.Set, 0,
                _ => 42, context, static () => true), Is.True);
            Assert.That(new[] { first.Values[0][0], second.Values[0][0], third.Values[0][0] },
                Is.EqualTo(new[] { 42, 42, 42 }));
            Assert.That(firstDocument.IsDirty, Is.True);
            Assert.That(secondDocument.IsDirty, Is.False);
            Assert.That(replacement.IsDirty, Is.False);
            Assert.That(thirdDocument.IsDirty, Is.True);
            Assert.That(second.SourceAsset, Is.SameAs(replacement));
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(first.Values[0][0], Is.EqualTo(42));
            Assert.That(third.Values[0][0], Is.EqualTo(42));
        }
        finally
        {
            firstDocument.PropertyChanged -= replacePeer;
            Undo.ClearHistory();
            context.Retire();
            first.SourceAsset = first; second.SourceAsset = second; third.SourceAsset = third;
            first.Destroy(true); second.Destroy(true); third.Destroy(true);
            firstDocument.Destroy(true); secondDocument.Destroy(true);
            thirdDocument.Destroy(true); replacement.Destroy(true);
        }
    }

    [TestCase("Owner", false)]
    [TestCase("Owner", true)]
    [TestCase("Node", false)]
    [TestCase("Node", true)]
    [TestCase("Document", false)]
    [TestCase("Document", true)]
    public void NestedCommandAndReplayRejectRegeneratedOwnerLifetimes(string changed, bool replay)
    {
        SceneNode node = new("Nested owner");
        NestedLifetimeComponent owner = node.AddComponent<NestedLifetimeComponent>()!;
        XRScene scene = new("Scene", node);
        XRWorld document = new("Source document");
        scene.SourceAsset = document;
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        var (paths, context) = CaptureDirtyPaths(owner);
        try
        {
            if (replay)
                Assert.That(EditorNestedListMutation.Apply(paths, EEditorListMutation.Set, 0,
                    _ => 42, context, static () => true), Is.True);
            document.ClearDirty();
            switch (changed)
            {
                case "Owner": owner.Generate(); break;
                case "Node": node.Generate(); break;
                default: document.Generate(); break;
            }
            Assert.That(paths[0].IsOwnerPathCurrent(), Is.False);
            if (replay)
                Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            else
            {
                Assert.Throws<InvalidOperationException>(() => EditorNestedListMutation.Apply(paths,
                    EEditorListMutation.Set, 0, _ => 42, context, static () => true));
                Assert.That(Undo.CanUndo, Is.False);
            }
            Assert.That(owner.Values[0][0], Is.EqualTo(replay ? 42 : 1));
            Assert.That(document.IsDirty, Is.False);
        }
        finally
        {
            Undo.ClearHistory();
            context.Retire();
            world.Dispose();
            node.Destroy(true);
            scene.SourceAsset = scene;
            document.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    private static (EditorNestedListSourceLease[] Paths, EditorNestedListMutationContext Context)
        CaptureDirtyPaths(params object[] owners)
    {
        List<EditorNestedListSourceLease> paths = [];
        Type openBinding = typeof(InspectorPropertyEditors.CollectionTypes)
            .GetNestedType("ListElementBinding`1", BindingFlags.NonPublic)!;
        foreach (object owner in owners)
        {
            PropertyInfo property = owner.GetType().GetProperty("Values")!;
            IList outer = (IList)property.GetValue(owner)!;
            EditorListMutationContext outerRows = new();
            outerRows.Rows.Reset(outer.Count);
            Type bindingType = openBinding.MakeGenericType(typeof(List<int>));
            IEditorListElementBinding binding = (IEditorListElementBinding)Activator.CreateInstance(bindingType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DoNotWrapExceptions,
                binder: null, args: [property, owner, 0, outerRows.Rows.GetItemId(0), outerRows], culture: null)!;
            paths.Add(EditorNestedListSourceLease.Capture(binding));
        }
        EditorListMutationContext rows = new();
        rows.Rows.Reset(paths[0].Inner.Count);
        EditorNestedListSourceLease[] captured = [.. paths];
        return (captured, new(captured, rows));
    }

    private sealed class NestedDirtyAsset : XRAsset
    {
        public List<List<int>> Values { get; set; } = [[1]];
    }

    private sealed class NestedLifetimeComponent : XRComponent
    {
        public List<List<int>> Values { get; set; } = [[1]];
    }
}
