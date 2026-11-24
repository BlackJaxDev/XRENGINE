namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Names one nested command container that a flow command owns, such as a branch, a case, or a loop body.
    /// Tools use this record to walk the command tree without reflection.
    /// </summary>
    /// <param name="Name">The stable display name of the child container, for example <c>True</c>, <c>Case 2</c>, or <c>Body</c>.</param>
    /// <param name="Container">The nested command container.</param>
    /// <param name="CaseKey">The switch case key, or null when the child is not a keyed case.</param>
    public readonly record struct RenderCommandChildContainer(
        string Name,
        ViewportRenderCommandContainer Container,
        int? CaseKey = null);
}
