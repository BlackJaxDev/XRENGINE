using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIVirtualTableRefreshTests
{
    [Test]
    public void SameIdRevisionRefreshKeepsFocusedCellAndExactRowLease()
    {
        UIVirtualTableRefreshSource source = new();
        int bound = 0, refreshed = 0, unbound = 0;
        UIVirtualTableProvider<string> table = new(source, new UIVirtualTableColumn<string>(
            "value", "Value", 120,
            parent =>
            {
                SceneNode cell = new(parent);
                UIBoundableTransform bounds = cell.SetTransform<UIBoundableTransform>();
                cell.AddComponent<UIButtonComponent>();
                return bounds;
            },
            (cell, value, _) => { bound++; cell.SceneNode!.GetComponent<UIButtonComponent>()!.AccessibilityLabel = value; },
            cell =>
            {
                unbound++;
                UIButtonComponent button = cell.SceneNode!.GetComponent<UIButtonComponent>()!;
                button.IsFocused = false;
                button.AccessibilityLabel = null;
            },
            (cell, value, _) =>
            {
                refreshed++;
                cell.SceneNode!.GetComponent<UIButtonComponent>()!.AccessibilityLabel = value;
            }));
        SceneNode owner = new("Table");
        UIListTransform list = owner.SetTransform<UIListTransform>();
        list.ItemSize = 20;
        list.VirtualOverscan = 0;
        list.VirtualProvider = table;
        try
        {
            list.SynchronizeVirtualRows(20);
            list.TryGetRealizedVirtualRow(11, out UIBoundableTransform? row,
                out UIVirtualRowLease originalLease).ShouldBeTrue();
            UIButtonComponent button = row!.SceneNode!.FirstChild!.GetComponent<UIButtonComponent>()!;
            button.IsFocused = true;

            source.Replace(11, "Updated");
            list.SynchronizeVirtualRows(20);

            list.TryGetRealizedVirtualRow(11, out UIBoundableTransform? sameRow,
                out UIVirtualRowLease refreshedLease).ShouldBeTrue();
            sameRow.ShouldBeSameAs(row);
            refreshedLease.ShouldBe(originalLease);
            button.IsFocused.ShouldBeTrue();
            button.AccessibilityLabel.ShouldBe("Updated");
            bound.ShouldBe(1);
            refreshed.ShouldBe(1);
            unbound.ShouldBe(0);

            source.Replace(22, "Other item");
            list.SynchronizeVirtualRows(20);
            list.TryGetRealizedVirtualRow(22, out _, out UIVirtualRowLease replacementLease).ShouldBeTrue();
            replacementLease.Incarnation.ShouldNotBe(originalLease.Incarnation);
            button.IsFocused.ShouldBeFalse();
            bound.ShouldBe(2);
            refreshed.ShouldBe(1);
            unbound.ShouldBe(1);
        }
        finally { owner.Destroy(true); }
    }

    [Test]
    public void RefreshCallbackRetirementStopsLaterCellsAndClearsOldBinding()
    {
        UIVirtualTableRefreshSource source = new();
        SceneNode owner = new("Table");
        UIListTransform list = owner.SetTransform<UIListTransform>();
        int laterRefreshes = 0, unbound = 0;
        UIVirtualTableProvider<string> table = new(source,
            new UIVirtualTableColumn<string>("first", "First", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (_, _, _) => { }, _ => unbound++,
                (_, _, _) => list.VirtualProvider = null),
            new UIVirtualTableColumn<string>("second", "Second", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (_, _, _) => { }, _ => unbound++,
                (_, _, _) => laterRefreshes++));
        list.ItemSize = 20;
        list.VirtualOverscan = 0;
        list.VirtualProvider = table;
        try
        {
            list.SynchronizeVirtualRows(20);
            source.Replace(11, "Updated");
            list.SynchronizeVirtualRows(20);

            list.VirtualProvider.ShouldBeNull();
            laterRefreshes.ShouldBe(0);
            unbound.ShouldBe(2);
        }
        finally { owner.Destroy(true); }
    }

    [Test]
    public void ReentrantUnbindCannotClearTheReplacementItem()
    {
        UIVirtualTableRefreshSource source = new();
        UIVirtualTableProvider<string>? table = null;
        UIBoundableTransform? row = null;
        bool rebound = false;
        int firstUnbinds = 0;
        int replacementBinds = 0;
        int oldHandlerCalls = 0;
        int replacementHandlerCalls = 0;
        Dictionary<UIBoundableTransform, Action> handlers = [];
        void BindHandler(UIBoundableTransform cell, string value, ulong _)
        {
            Action handler = value == "First" ? () => oldHandlerCalls++ : () => replacementHandlerCalls++;
            source.Changed += handler;
            handlers.Add(cell, handler);
            if (value == "Second") replacementBinds++;
        }
        void UnbindHandler(UIBoundableTransform cell)
        {
            if (handlers.Remove(cell, out Action? handler))
                source.Changed -= handler;
        }
        table = new UIVirtualTableProvider<string>(source,
            new UIVirtualTableColumn<string>("first", "First", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                BindHandler,
                cell => { firstUnbinds++; UnbindHandler(cell); }),
            new UIVirtualTableColumn<string>("second", "Second", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                BindHandler,
                cell =>
                {
                    UnbindHandler(cell);
                    if (rebound) return;
                    rebound = true;
                    table!.BindRow(row!, 0, 22, 2);
                }));
        row = table!.CreateRow();
        try
        {
            table.BindRow(row!, 0, 11, 1);
            source.Replace(22, "Second");
            table.BindRow(row, 0, 22, 2);

            replacementBinds.ShouldBe(2);
            firstUnbinds.ShouldBe(1);
            handlers.Count.ShouldBe(2);
            source.RaiseChanged();
            oldHandlerCalls.ShouldBe(0);
            replacementHandlerCalls.ShouldBe(2);
            table.UnbindRow(row, 11, 1);
            firstUnbinds.ShouldBe(1);
            table.UnbindRow(row, 22, 2);
            firstUnbinds.ShouldBe(2);
            handlers.Count.ShouldBe(0);
        }
        finally { row!.SceneNode!.Destroy(true); }
    }

    [Test]
    public void FailedFallbackRefreshAttemptsRetiredCellCleanupOnce()
    {
        UIVirtualTableRefreshSource source = new();
        int unbinds = 0;
        UIVirtualTableProvider<string> table = new(source,
            new UIVirtualTableColumn<string>("value", "Value", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (_, _, _) => { },
                _ =>
                {
                    unbinds++;
                    throw new InvalidOperationException("Cell retired during refresh.");
                }));
        UIBoundableTransform row = table.CreateRow();
        try
        {
            table.BindRow(row, 0, 11, 1);
            source.Replace(11, "Updated");
            Should.Throw<InvalidOperationException>(() => table.BindRow(row, 0, 11, 1));
            unbinds.ShouldBe(1);
            table.UnbindRow(row, 11, 1);
            unbinds.ShouldBe(1);
        }
        finally { row.SceneNode!.Destroy(true); }
    }

    [Test]
    public void FailedInitialBindDoesNotUnbindUntouchedColumns()
    {
        UIVirtualTableRefreshSource source = new();
        int attemptedCleanup = 0, untouchedCleanup = 0;
        UIVirtualTableProvider<string> table = new(source,
            new UIVirtualTableColumn<string>("first", "First", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (_, _, _) => throw new InvalidOperationException("Initial bind failed."),
                _ => attemptedCleanup++),
            new UIVirtualTableColumn<string>("second", "Second", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (_, _, _) => { }, _ => untouchedCleanup++));
        UIBoundableTransform row = table.CreateRow();
        try
        {
            Should.Throw<InvalidOperationException>(() => table.BindRow(row, 0, 11, 1));
            attemptedCleanup.ShouldBe(1);
            untouchedCleanup.ShouldBe(0);
        }
        finally { row.SceneNode!.Destroy(true); }
    }

    [Test]
    public void SourceRevisionDuringInitialBindRetiresPartialCellsBeforeRetry()
    {
        UIVirtualTableRefreshSource source = new();
        int unboundFirst = 0, unboundSecond = 0, oldCalls = 0, currentCalls = 0;
        bool advanceOnce = true;
        Dictionary<UIBoundableTransform, Action> handlers = [];
        UIVirtualTableProvider<string> table = new(source,
            new UIVirtualTableColumn<string>("first", "First", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (cell, _, _) =>
                {
                    Action handler = advanceOnce ? () => oldCalls++ : () => currentCalls++;
                    source.Changed += handler;
                    handlers.Add(cell, handler);
                    if (advanceOnce) { advanceOnce = false; source.AdvanceRevision(); }
                },
                cell => { unboundFirst++; source.Changed -= handlers[cell]; handlers.Remove(cell); }),
            new UIVirtualTableColumn<string>("second", "Second", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (cell, _, _) =>
                {
                    Action handler = () => currentCalls++;
                    source.Changed += handler;
                    handlers.Add(cell, handler);
                },
                cell => { unboundSecond++; source.Changed -= handlers[cell]; handlers.Remove(cell); }));
        UIBoundableTransform row = table.CreateRow();
        try
        {
            table.BindRow(row, 0, 11, 1);
            handlers.Count.ShouldBe(0);
            unboundFirst.ShouldBe(1);
            unboundSecond.ShouldBe(0);
            source.RaiseChanged();
            oldCalls.ShouldBe(0);

            table.BindRow(row, 0, 11, 1);
            handlers.Count.ShouldBe(2);
            source.RaiseChanged();
            currentCalls.ShouldBe(2);
        }
        finally { row.SceneNode!.Destroy(true); }
    }

    [Test]
    public void SourceRevisionDuringRefreshRetiresOldCellsBeforeExplicitRebind()
    {
        UIVirtualTableRefreshSource source = new();
        int unbound = 0, secondRefreshes = 0, bound = 0;
        int oldHandlerCalls = 0, currentHandlerCalls = 0;
        Dictionary<UIBoundableTransform, Action> handlers = [];
        bool advanceOnce = true;
        void BindHandler(UIBoundableTransform cell, string _, ulong __)
        {
            Action handler = bound < 2 ? () => oldHandlerCalls++ : () => currentHandlerCalls++;
            bound++;
            source.Changed += handler;
            handlers.Add(cell, handler);
        }
        void UnbindHandler(UIBoundableTransform cell)
        {
            unbound++;
            source.Changed -= handlers[cell];
            handlers.Remove(cell);
        }
        UIVirtualTableProvider<string> table = new(source,
            new UIVirtualTableColumn<string>("first", "First", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                BindHandler, UnbindHandler,
                (_, _, _) =>
                {
                    if (advanceOnce) { advanceOnce = false; source.AdvanceRevision(); }
                }),
            new UIVirtualTableColumn<string>("second", "Second", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                BindHandler, UnbindHandler,
                (_, _, _) => secondRefreshes++));
        UIBoundableTransform row = table.CreateRow();
        try
        {
            table.BindRow(row, 0, 11, 1);
            source.AdvanceRevision();
            table.BindRow(row, 0, 11, 1);
            unbound.ShouldBe(2);
            handlers.Count.ShouldBe(0);
            source.RaiseChanged();
            oldHandlerCalls.ShouldBe(0);
            secondRefreshes.ShouldBe(0);
            table.BindRow(row, 0, 11, 1);
            bound.ShouldBe(4);
            handlers.Count.ShouldBe(2);
            source.RaiseChanged();
            currentHandlerCalls.ShouldBe(2);
            secondRefreshes.ShouldBe(0);
        }
        finally { row.SceneNode!.Destroy(true); }
    }

    [Test]
    public void SourceGetterReentryCannotOverwriteNestedBinding()
    {
        UIVirtualTableRefreshSource source = new();
        int binds = 0, unbinds = 0;
        UIVirtualTableProvider<string> table = new(source,
            new UIVirtualTableColumn<string>("value", "Value", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (_, _, _) => binds++, _ => unbinds++));
        UIBoundableTransform row = table.CreateRow();
        source.OnGetItem = () =>
        {
            source.OnGetItem = null;
            source.Replace(22, "Replacement");
            table.BindRow(row, 0, 22, 2);
        };
        try
        {
            table.BindRow(row, 0, 11, 1);
            binds.ShouldBe(1);
            table.UnbindRow(row, 11, 1);
            unbinds.ShouldBe(0);
            table.UnbindRow(row, 22, 2);
            binds.ShouldBe(1);
            unbinds.ShouldBe(1);
        }
        finally { row.SceneNode!.Destroy(true); }
    }

    [Test]
    public void FallbackRefreshDefersReplacementUntilOldCellClearReturns()
    {
        UIVirtualTableRefreshSource source = new();
        UIVirtualTableProvider<string>? table = null;
        UIBoundableTransform? row = null;
        List<string> operations = [];
        bool replace = true;
        table = new UIVirtualTableProvider<string>(source,
            new UIVirtualTableColumn<string>("value", "Value", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (cell, value, _) =>
                {
                    operations.Add("bind " + value);
                    cell.SceneNode!.Name = value;
                },
                cell =>
                {
                    operations.Add("unbind begin");
                    if (replace)
                    {
                        replace = false;
                        source.Replace(22, "Replacement");
                        table!.BindRow(row!, 0, 22, 2);
                    }
                    cell.SceneNode!.Name = "cleared";
                    operations.Add("unbind clear");
                }));
        row = table.CreateRow();
        try
        {
            table.BindRow(row, 0, 11, 1);
            source.AdvanceRevision();
            table.BindRow(row, 0, 11, 1);

            UIBoundableTransform cell = (UIBoundableTransform)row.Children[0];
            cell.SceneNode!.Name.ShouldBe("Replacement");
            operations.ShouldBe(new[] { "bind First", "unbind begin", "unbind clear", "bind Replacement" });
            table.UnbindRow(row, 11, 1);
            cell.SceneNode.Name.ShouldBe("Replacement");
        }
        finally { row!.SceneNode!.Destroy(true); }
    }

    [Test]
    public void BindCellDefersReplacementUntilOldCallbackFinishesWriting()
    {
        UIVirtualTableRefreshSource source = new();
        UIVirtualTableProvider<string>? table = null;
        UIBoundableTransform? row = null;
        List<string> operations = [];
        table = new UIVirtualTableProvider<string>(source,
            new UIVirtualTableColumn<string>("value", "Value", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (cell, value, _) =>
                {
                    if (value == "First")
                    {
                        source.Replace(22, "Replacement");
                        table!.BindRow(row!, 0, 22, 2);
                        cell.SceneNode!.Name = "old callback write";
                        operations.Add("old write");
                    }
                    else
                    {
                        cell.SceneNode!.Name = value;
                        operations.Add("replacement bind");
                    }
                },
                _ => operations.Add("old unbind")));
        row = table.CreateRow();
        try
        {
            table.BindRow(row, 0, 11, 1);
            ((UIBoundableTransform)row.Children[0]).SceneNode!.Name.ShouldBe("Replacement");
            operations.ShouldBe(new[] { "old write", "old unbind", "replacement bind" });
        }
        finally { row!.SceneNode!.Destroy(true); }
    }

    [Test]
    public void RefreshCellDefersReplacementUntilOldCallbackFinishesWriting()
    {
        UIVirtualTableRefreshSource source = new();
        UIVirtualTableProvider<string>? table = null;
        UIBoundableTransform? row = null;
        List<string> operations = [];
        table = new UIVirtualTableProvider<string>(source,
            new UIVirtualTableColumn<string>("value", "Value", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (cell, value, _) =>
                {
                    cell.SceneNode!.Name = value;
                    operations.Add("bind " + value);
                },
                _ => operations.Add("old unbind"),
                (cell, _, _) =>
                {
                    source.Replace(22, "Replacement");
                    table!.BindRow(row!, 0, 22, 2);
                    cell.SceneNode!.Name = "old refresh write";
                    operations.Add("old refresh write");
                }));
        row = table.CreateRow();
        try
        {
            table.BindRow(row, 0, 11, 1);
            source.AdvanceRevision();
            table.BindRow(row, 0, 11, 1);
            ((UIBoundableTransform)row.Children[0]).SceneNode!.Name.ShouldBe("Replacement");
            operations.ShouldBe(new[]
                { "bind First", "old refresh write", "old unbind", "bind Replacement" });
        }
        finally { row!.SceneNode!.Destroy(true); }
    }

    [Test]
    public void ProviderTeardownDuringCellCallbackWaitsForCallbackThenUnbinds()
    {
        UIVirtualTableRefreshSource source = new();
        SceneNode owner = new("Table");
        UIListTransform list = owner.SetTransform<UIListTransform>();
        int unbinds = 0;
        List<string> operations = [];
        UIVirtualTableProvider<string> table = new(source,
            new UIVirtualTableColumn<string>("value", "Value", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (cell, _, _) =>
                {
                    list.VirtualProvider = null;
                    operations.Add("provider detached");
                    cell.SceneNode!.Name = "old callback write";
                    operations.Add("old callback write");
                },
                cell =>
                {
                    unbinds++;
                    operations.Add("unbind observed " + cell.SceneNode!.Name);
                    cell.SceneNode!.Name = "cleared";
                    operations.Add("unbind cleared");
                }));
        list.ItemSize = 20;
        list.VirtualOverscan = 0;
        list.VirtualProvider = table;
        try
        {
            list.SynchronizeVirtualRows(20);
            list.VirtualProvider.ShouldBeNull();
            unbinds.ShouldBe(1);
            operations.ShouldBe(new[]
                { "provider detached", "old callback write", "unbind observed old callback write", "unbind cleared" });
        }
        finally { owner.Destroy(true); }
    }

    [Test]
    public void ThrowAfterReplacementRequestRetiresOldCellWithoutAdmittingReplacement()
    {
        UIVirtualTableRefreshSource source = new();
        UIVirtualTableProvider<string>? table = null;
        UIBoundableTransform? row = null;
        int oldUnbinds = 0, replacementBinds = 0;
        table = new UIVirtualTableProvider<string>(source,
            new UIVirtualTableColumn<string>("value", "Value", 80,
                parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
                (_, value, _) =>
                {
                    if (value == "Replacement")
                    {
                        replacementBinds++;
                        return;
                    }
                    source.Replace(22, "Replacement");
                    table!.BindRow(row!, 0, 22, 2);
                    throw new InvalidOperationException("Old cell bind failed.");
                },
                _ => oldUnbinds++));
        row = table.CreateRow();
        try
        {
            Should.Throw<InvalidOperationException>(() => table.BindRow(row, 0, 11, 1));
            oldUnbinds.ShouldBe(1);
            replacementBinds.ShouldBe(0);
            table.UnbindRow(row, 22, 2);
            oldUnbinds.ShouldBe(1);
            table.BindRow(row, 0, 22, 2);
            replacementBinds.ShouldBe(1);
        }
        finally { row!.SceneNode!.Destroy(true); }
    }
}
