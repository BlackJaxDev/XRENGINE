namespace XREngine.Editor;

/// <summary>Pure, versioned editor dock operations. Window hosts render this tree.</summary>
public sealed class NativeDockTree
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public NativeDockNode Root { get; set; } = new();
    public List<string> ClosedPanels { get; set; } = [];

    public static NativeDockTree FromLegacy(NativeDockLayout legacy)
    {
        NativeDockNode tools = Stack();
        foreach (int index in legacy.TabOrder)
        {
            string panel = NativeDockPanel.LegacyTools[index];
            if ((legacy.OpenMask & (1 << index)) != 0)
                tools.Tabs.Add(panel);
        }
        tools.Selected = legacy.SelectedTab is >= 0 and < 4
            ? NativeDockPanel.LegacyTools[legacy.SelectedTab] : tools.Tabs.FirstOrDefault();
        NativeDockTree tree = new()
        {
            Root = Split(false, 300, Stack(NativeDockPanel.Hierarchy),
                Split(false, 300,
                    Split(true, legacy.BottomSize, Stack(NativeDockPanel.Scene), tools,
                        firstFixedSize: false),
                    Stack(NativeDockPanel.Inspector), firstFixedSize: false))
        };
        foreach (string panel in NativeDockPanel.LegacyTools)
            if (!tools.Tabs.Contains(panel))
                tree.ClosedPanels.Add(panel);
        return tree;
    }

    /// <summary>Migrates an old detached tool window without importing the primary scene layout.</summary>
    public static NativeDockTree FromLegacyDetached(NativeDockLayout legacy, int fallbackPanel)
    {
        NativeDockNode tools = Stack();
        foreach (int index in legacy.TabOrder)
            if ((legacy.OpenMask & (1 << index)) != 0)
                tools.Tabs.Add(NativeDockPanel.LegacyTools[index]);
        string fallback = NativeDockPanel.LegacyTools[Math.Clamp(fallbackPanel, 0,
            NativeDockPanel.LegacyTools.Length - 1)];
        if (tools.Tabs.Count == 0)
            tools.Tabs.Add(fallback);
        string selected = legacy.SelectedTab is >= 0 and < 4
            ? NativeDockPanel.LegacyTools[legacy.SelectedTab] : fallback;
        tools.Selected = tools.Tabs.Contains(selected) ? selected : tools.Tabs[0];
        return new NativeDockTree { Root = tools }.Sanitize();
    }

    public NativeDockTree Sanitize()
    {
        if (Version != CurrentVersion || Root is null)
            throw new InvalidOperationException("Unsupported native dock tree version.");
        ClosedPanels ??= [];
        HashSet<string> allowed = [.. NativeDockPanel.All];
        HashSet<string> seenPanels = new(StringComparer.Ordinal);
        HashSet<string> seenNodes = new(StringComparer.Ordinal);
        int nodes = 0;
        void Visit(NativeDockNode node, int depth)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
                node.Id = Guid.NewGuid().ToString("N");
            if (++nodes > 64 || depth > 16 || !seenNodes.Add(node.Id))
                throw new InvalidOperationException("Native dock tree is cyclic or too deep.");
            if ((node.First is null) != (node.Second is null))
                throw new InvalidOperationException("Native dock split is incomplete.");
            if (node.IsSplit)
            {
                node.FixedSize = float.IsFinite(node.FixedSize)
                    ? Math.Clamp(node.FixedSize, 96, 2000) : 300;
                Visit(node.First!, depth + 1);
                Visit(node.Second!, depth + 1);
                node.Tabs.Clear();
                node.Selected = null;
            }
            else
            {
                node.First = null;
                node.Second = null;
                node.Tabs ??= [];
                node.Tabs = node.Tabs.Where(panel => allowed.Contains(panel) && seenPanels.Add(panel)).ToList();
                if (node.Selected is null || !node.Tabs.Contains(node.Selected))
                    node.Selected = node.Tabs.FirstOrDefault();
            }
        }
        Visit(Root, 0);
        ClosedPanels = ClosedPanels.Where(panel => allowed.Contains(panel) && seenPanels.Add(panel)).ToList();
        foreach (string panel in NativeDockPanel.All)
            if (seenPanels.Add(panel))
                ClosedPanels.Add(panel);
        return this;
    }

    public IEnumerable<NativeDockNode> Stacks()
    {
        Stack<NativeDockNode> pending = new();
        pending.Push(Root);
        while (pending.Count > 0)
        {
            NativeDockNode node = pending.Pop();
            if (!node.IsSplit)
                yield return node;
            else
            {
                pending.Push(node.Second!);
                pending.Push(node.First!);
            }
        }
    }

    public bool Move(string panel, string targetStackId, int index)
    {
        NativeDockNode? target = Stacks().FirstOrDefault(stack => stack.Id == targetStackId);
        if (target is null || !NativeDockPanel.All.Contains(panel))
            return false;
        RemovePanel(panel);
        target.Tabs.Insert(Math.Clamp(index, 0, target.Tabs.Count), panel);
        target.Selected = panel;
        Root = CollapseEmptyStacks(Root);
        return true;
    }

    public bool Close(string panel)
    {
        if (!RemovePanel(panel))
            return false;
        ClosedPanels.Add(panel);
        Root = CollapseEmptyStacks(Root);
        return true;
    }

    public bool Reopen(string panel, string targetStackId)
        => ClosedPanels.Contains(panel) && Move(panel, targetStackId, int.MaxValue);

    public bool SplitPanel(string panel, string targetStackId, bool vertical, bool before)
    {
        NativeDockNode? target = Stacks().FirstOrDefault(stack => stack.Id == targetStackId);
        if (target is null || !NativeDockPanel.All.Contains(panel) ||
            (target.Tabs.Count == 1 && target.Tabs.Contains(panel)))
            return false;
        if (!RemovePanel(panel))
            return false;
        Root = CollapseEmptyStacks(Root, target);
        NativeDockNode stack = Stack(panel);
        NativeDockNode wrapper = before
            ? Split(vertical, 300, stack, target)
            : Split(vertical, 300, target, stack, firstFixedSize: false);
        if (ReferenceEquals(Root, target))
            Root = wrapper;
        else if (!ReplaceChild(Root, target, wrapper))
            throw new InvalidOperationException("Dock target disappeared during split.");
        return true;
    }

    public bool Resize(string splitId, float size)
    {
        NativeDockNode? split = Find(Root, splitId);
        if (split is null || !split.IsSplit || !float.IsFinite(size))
            return false;
        split.FixedSize = Math.Clamp(size, 96, 2000);
        return true;
    }

    private bool RemovePanel(string panel)
    {
        bool removed = ClosedPanels.Remove(panel);
        foreach (NativeDockNode stack in Stacks())
        {
            if (!stack.Tabs.Remove(panel))
                continue;
            removed = true;
            if (stack.Selected == panel)
                stack.Selected = stack.Tabs.FirstOrDefault();
        }
        return removed;
    }

    private static NativeDockNode? Find(NativeDockNode node, string id)
        => node.Id == id ? node : node.IsSplit
            ? Find(node.First!, id) ?? Find(node.Second!, id) : null;

    private static NativeDockNode CollapseEmptyStacks(NativeDockNode node, NativeDockNode? preserve = null)
    {
        if (!node.IsSplit)
            return node;
        node.First = CollapseEmptyStacks(node.First!, preserve);
        node.Second = CollapseEmptyStacks(node.Second!, preserve);
        bool firstEmpty = !ReferenceEquals(node.First, preserve) &&
            !node.First.IsSplit && node.First.Tabs.Count == 0;
        bool secondEmpty = !ReferenceEquals(node.Second, preserve) &&
            !node.Second.IsSplit && node.Second.Tabs.Count == 0;
        if (firstEmpty && !secondEmpty)
            return node.Second;
        if (secondEmpty)
            return node.First;
        return node;
    }

    private static bool ReplaceChild(NativeDockNode root, NativeDockNode child, NativeDockNode replacement)
    {
        if (!root.IsSplit)
            return false;
        if (ReferenceEquals(root.First, child))
        {
            root.First = replacement;
            return true;
        }
        if (ReferenceEquals(root.Second, child))
        {
            root.Second = replacement;
            return true;
        }
        return ReplaceChild(root.First!, child, replacement) || ReplaceChild(root.Second!, child, replacement);
    }

    private static NativeDockNode Stack(params string[] panels)
        => new() { Tabs = [.. panels], Selected = panels.FirstOrDefault() };

    private static NativeDockNode Split(bool vertical, float size, NativeDockNode first,
        NativeDockNode second, bool firstFixedSize = true)
        => new()
        {
            Vertical = vertical, FixedSize = size, FirstFixedSize = firstFixedSize,
            First = first, Second = second
        };
}
