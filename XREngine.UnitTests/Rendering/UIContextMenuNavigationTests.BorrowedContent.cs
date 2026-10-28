using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

public sealed partial class UIContextMenuNavigationTests
{
    [Test]
    public void HideReturnsBorrowedContentAndDestroysOwnedContent()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode home = CreateBorrowHome(root, out SceneNode borrowed);
            SceneNode? owned = null;
            menu.ShowContent(Vector2.Zero, 40, content =>
            {
                owned = content.NewChild();
                owned.Name = "Owned menu row";
                menu.BorrowContent(content, borrowed);
            });
            borrowed.Parent.ShouldNotBe(home);

            menu.Hide();

            borrowed.IsDestroyed.ShouldBeFalse();
            borrowed.Parent.ShouldBe(home);
            owned.ShouldNotBeNull();
            (owned!.IsDestroyed || owned.IsDestroyQueued).ShouldBeTrue();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void BorrowedContentWithoutItsHomeIsDetachedNotDestroyed()
    {
        SceneNode root = CreateCanvas(out _);
        SceneNode? borrowed = null;
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode home = CreateBorrowHome(root, out SceneNode node);
            borrowed = node;
            menu.ShowContent(Vector2.Zero, 40, content => menu.BorrowContent(content, node));
            home.Destroy(true);

            menu.Hide();

            node.IsDestroyed.ShouldBeFalse();
            node.Parent.ShouldBeNull();
        }
        finally
        {
            root.Destroy(true);
            borrowed?.Destroy(true);
        }
    }

    [Test]
    public void BorrowedContentMovedOutOfTheMenuStaysWhereItWasMoved()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            CreateBorrowHome(root, out SceneNode borrowed);
            SceneNode elsewhere = new(root) { Name = "Elsewhere" };
            elsewhere.SetTransform<UIBoundableTransform>();
            menu.ShowContent(Vector2.Zero, 40, content => menu.BorrowContent(content, borrowed));
            borrowed.Transform.SetParent(elsewhere.Transform, false,
                XREngine.Scene.Transforms.EParentAssignmentMode.Immediate);

            menu.Hide();

            borrowed.IsDestroyed.ShouldBeFalse();
            borrowed.Parent.ShouldBe(elsewhere);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DestroyedMenuReturnsBorrowedContent()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode home = CreateBorrowHome(root, out SceneNode borrowed);
            menu.ShowContent(Vector2.Zero, 40, content => menu.BorrowContent(content, borrowed));

            menu.SceneNode.Destroy(true);

            borrowed.IsDestroyed.ShouldBeFalse();
            borrowed.Parent.ShouldBe(home);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void BorrowContentOutsideTheContentBuilderIsRejected()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode home = CreateBorrowHome(root, out SceneNode borrowed);
            menu.Show(Vector2.Zero, new ContextMenuItem("Ordinary", null));

            Should.Throw<InvalidOperationException>(() => menu.BorrowContent(menu.SceneNode, borrowed));
            borrowed.Parent.ShouldBe(home);

            menu.ShowContent(Vector2.Zero, 40, content =>
                Should.Throw<InvalidOperationException>(() => menu.BorrowContent(content, menu.SceneNode)));
        }
        finally { root.Destroy(true); }
    }

    private static SceneNode CreateBorrowHome(SceneNode root, out SceneNode borrowed)
    {
        SceneNode home = new(root) { Name = "Borrowed node home" };
        home.SetTransform<UIBoundableTransform>();
        borrowed = new SceneNode(home) { Name = "Borrowed node" };
        borrowed.SetTransform<UIBoundableTransform>();
        return home;
    }
}
