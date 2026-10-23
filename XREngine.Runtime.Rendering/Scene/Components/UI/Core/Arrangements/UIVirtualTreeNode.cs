using System.Collections.Generic;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Holds logical tree state. A row may display this node without owning it.
    /// </summary>
    public sealed class UIVirtualTreeNode(ulong id, string name, bool isSection)
    {
        public ulong Id { get; } = id;
        public string Name { get; internal set; } = name;
        public bool IsSection { get; } = isSection;
        public bool IsExpanded { get; internal set; }
        public ulong ParentId { get; internal set; }
        public List<ulong> Children { get; } = [];
    }
}
