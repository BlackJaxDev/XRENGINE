namespace XREngine.Animation
{
    public enum EAnimTreeTraversalMethod
    {
        /// <summary>
        /// All members are animated at the same time.
        /// </summary>
        Parallel,
        /// <summary>
        /// Members are animated sequentially in order of appearance, parent-down.
        /// Root-Child[0]-Grandchild[0]-Child[1]-Grandchild[0]
        /// </summary>
        BreadthFirst,
        /// <summary>
        /// Left-Root-Right
        /// </summary>
        DepthFirstInOrder,
        /// <summary>
        /// Left-Right-Root
        /// </summary>
        DepthFirstPreOrder,
        /// <summary>
        /// Root-Left-Right
        /// </summary>
        DepthFirstPostOrder
    }
}
