using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

public sealed partial class UIContextMenuNavigationTests
{
    [TestCase("row")]
    [TestCase("background")]
    [TestCase("label")]
    [TestCase("arrow")]
    [TestCase("separator")]
    [TestCase("line")]
    public void FailedFactoryRetiresItsDetachedMenuLeaf(string boundary)
    {
        SceneNode root = CreateCanvas(out var input);
        XRComponent? failed = null;
        SceneNode? failedNode = null;
        var failure = new InvalidOperationException("Factory observer failed.");
        int texts = 0, materials = 0;
        void OnCreated(XRComponent component)
        {
            if (component.SceneNode.Name != "ContextMenuItem") return;
            if (component is UITextComponent) texts++;
            if (component is UIMaterialComponent) materials++;
            bool match = boundary switch
            {
                "row" => component is UIContextMenuItemComponent,
                "background" or "separator" => component is UIMaterialComponent && materials == 1,
                "label" => component is UITextComponent && texts == 1,
                "arrow" => component is UITextComponent && texts == 2,
                "line" => component is UIMaterialComponent && materials == 2,
                _ => false
            };
            if (!match) return;
            failed = component;
            failedNode = component.SceneNode;
            failedNode.Parent.ShouldBeNull();
            throw failure;
        }
        try
        {
            var menu = CreateMenu(root);
            XRComponent.ComponentCreated += OnCreated;
            try
            {
                Should.Throw<InvalidOperationException>(() => menu.Show(Vector2.Zero,
                    boundary is "separator" or "line" ? ContextMenuItem.Separator() :
                    ContextMenuItem.Submenu("Parent", [new ContextMenuItem("Child", null)])))
                    .ShouldBeSameAs(failure);
            }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            failed.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            failedNode.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            menu.FailedConstructionLeaves.ShouldBeEmpty();
            menu.IsOpen.ShouldBeFalse();
            Rows(menu).ShouldBeEmpty();
            input.SubscriberCount.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TextConfigurationCancellationStopsLaterWritesAndRetiresTheLeaf(bool replace)
    {
        SceneNode root = CreateCanvas(out _);
        UITextComponent? oldLabel = null;
        SceneNode? oldLeaf = null;
        try
        {
            var menu = CreateMenu(root);
            void OnCreated(XRComponent component)
            {
                if (oldLabel is not null || component is not UITextComponent label) return;
                oldLabel = label;
                oldLeaf = label.SceneNode;
                label.FontSize = 123;
                label.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(UITextComponent.Text)) return;
                    if (replace) menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));
                    else menu.Hide();
                };
            }
            XRComponent.ComponentCreated += OnCreated;
            try { menu.Show(Vector2.Zero, new ContextMenuItem("Old", null)); }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            oldLabel.ShouldNotBeNull().FontSize.ShouldBe(123);
            oldLabel.IsDestroyed.ShouldBeTrue();
            oldLeaf.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            menu.FailedConstructionLeaves.ShouldBeEmpty();
            menu.IsOpen.ShouldBe(replace);
            if (replace) Rows(menu).Single().Item!.Label.ShouldBe("Replacement");
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FactoryAdoptionKeepsTheTransferredLeafAndItsUninsertedComponent(bool hide)
    {
        SceneNode root = CreateCanvas(out _);
        UIContextMenuItemComponent? adopted = null;
        SceneNode? adoptedNode = null;
        try
        {
            var menu = CreateMenu(root);
            void OnCreated(XRComponent component)
            {
                if (adopted is not null || component is not UIContextMenuItemComponent row) return;
                adopted = row;
                adoptedNode = row.SceneNode;
                adoptedNode.Parent = root;
                if (hide) menu.Hide();
            }
            XRComponent.ComponentCreated += OnCreated;
            try { Should.Throw<InvalidOperationException>(() => menu.Show(Vector2.Zero, new ContextMenuItem("Old", null))); }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            adopted.ShouldNotBeNull().IsDestroyed.ShouldBeFalse();
            adoptedNode.ShouldNotBeNull().Parent.ShouldBeSameAs(root);
            adoptedNode.IsDestroyed.ShouldBeFalse();
            adoptedNode.GetComponent<UIContextMenuItemComponent>().ShouldBeNull();
            adoptedNode.GetComponent<UIMaterialComponent>().ShouldBeNull();
            menu.FailedConstructionLeaves.Count.ShouldBe(1);
            menu.FailedConstructionLeaves[0].Failure.ShouldNotBeNull();
            menu.IsOpen.ShouldBeFalse();
        }
        finally
        {
            if (adopted is { IsDestroyed: false }) adopted.Destroy(true);
            root.Destroy(true);
        }
    }

    [TestCase("row")]
    [TestCase("label")]
    [TestCase("arrow")]
    [TestCase("separator")]
    [TestCase("line")]
    public void AttachmentTransferStopsTheBuilderWithoutDestroyingTheForeignLeaf(string boundary)
    {
        SceneNode root = CreateCanvas(out _);
        SceneNode? transferred = null;
        int texts = 0, materials = 0;
        void OnCreated(XRComponent component)
        {
            if (component.SceneNode.Name != "ContextMenuItem") return;
            if (component is UITextComponent) texts++;
            if (component is UIMaterialComponent) materials++;
            bool match = boundary switch
            {
                "row" => component is UIContextMenuItemComponent,
                "label" => component is UITextComponent && texts == 1,
                "arrow" => component is UITextComponent && texts == 2,
                "separator" => component is UIMaterialComponent && materials == 1,
                "line" => component is UIMaterialComponent && materials == 2,
                _ => false
            };
            if (!match) return;
            SceneNode leaf = component.SceneNode;
            leaf.Transform.PropertyChanged += (_, args) =>
            {
                if (transferred is not null || args.PropertyName != nameof(TransformBase.Parent) || leaf.Parent is null) return;
                transferred = leaf;
                leaf.Parent = root;
            };
        }
        try
        {
            var menu = CreateMenu(root);
            XRComponent.ComponentCreated += OnCreated;
            try
            {
                Should.Throw<InvalidOperationException>(() => menu.Show(Vector2.Zero,
                    boundary is "separator" or "line" ? ContextMenuItem.Separator() :
                    ContextMenuItem.Submenu("Parent", [new ContextMenuItem("Child", null)])));
            }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            transferred.ShouldNotBeNull().IsDestroyed.ShouldBeFalse();
            transferred.Parent.ShouldBeSameAs(root);
            transferred.Transform.Children.Count.ShouldBe(0);
            menu.IsOpen.ShouldBeFalse();
            menu.FailedConstructionLeaves.Count.ShouldBe(1);
        }
        finally { root.Destroy(true); }
    }
    [TestCase(false)]
    [TestCase(true)]
    public void ChangedRowLifetimeRejectsTheDetachedLabelBeforeTextWrites(bool arrow)
    {
        SceneNode root = CreateCanvas(out _);
        UITextComponent? stale = null;
        SceneNode? staleNode = null;
        UIContextMenuItemComponent? row = null;
        int texts = 0;
        try
        {
            var menu = CreateMenu(root);
            void OnCreated(XRComponent component)
            {
                if (component is UIContextMenuItemComponent created) row = created;
                if (component is not UITextComponent label || ++texts != (arrow ? 2 : 1)) return;
                stale = label;
                staleNode = label.SceneNode;
                row.ShouldNotBeNull().Generate();
            }
            XRComponent.ComponentCreated += OnCreated;
            try
            {
                Should.Throw<InvalidOperationException>(() => menu.Show(Vector2.Zero,
                    ContextMenuItem.Submenu("Parent", [new ContextMenuItem("Child", null)])));
            }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            stale.ShouldNotBeNull().Text.ShouldBeNullOrEmpty();
            stale.IsDestroyed.ShouldBeTrue();
            staleNode.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            menu.FailedConstructionLeaves.ShouldBeEmpty();
            menu.IsOpen.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChangedContentRootLifetimeRejectsTheDetachedRow(bool queueTransform)
    {
        SceneNode root = CreateCanvas(out _);
        SceneNode? failed = null;
        SceneNode? foreignContent = null;
        try
        {
            var menu = CreateMenu(root);
            void OnCreated(XRComponent component)
            {
                if (component is not UIContextMenuItemComponent) return;
                failed = component.SceneNode;
                foreignContent = menu.SceneNode.FirstChild!;
                if (queueTransform) foreignContent.Transform.Destroy();
                else foreignContent.Generate();
            }
            XRComponent.ComponentCreated += OnCreated;
            try { Should.Throw<InvalidOperationException>(() => menu.Show(Vector2.Zero, new ContextMenuItem("Old", null))); }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            failed.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            foreignContent.ShouldNotBeNull().IsDestroyed.ShouldBeFalse();
            Rows(menu).ShouldBeEmpty();
            menu.FailedConstructionLeaves.ShouldBeEmpty();
            menu.IsOpen.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

}
