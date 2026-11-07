using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>
/// One change to the command chain of a base pipeline. The patch finds its target by anchor id, by structural path,
/// or by <see cref="RenderCommandPaths.RootTarget"/>. Commands are script text, so the patch compiles new commands each
/// time the base builds its chain.
/// </summary>
public sealed class RenderCommandPatch
{
    /// <summary>Gets or sets the change kind.</summary>
    public ERenderCommandPatchKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the target: an anchor id such as <c>post.bloom</c>, a structural path that starts with
    /// <c>path:</c>, or <c>root</c> for the top container.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Gets or sets the script text of the new commands of an insert, replace, or append.</summary>
    public string CommandText { get; set; } = string.Empty;

    /// <summary>Gets or sets the property that <see cref="ERenderCommandPatchKind.SetProperty"/> changes.</summary>
    public string? PropertyName { get; set; }

    /// <summary>Gets or sets the script value text that <see cref="ERenderCommandPatchKind.SetProperty"/> assigns.</summary>
    public string? ValueText { get; set; }

    /// <summary>
    /// Gets or sets the condition of <see cref="ERenderCommandPatchKind.SetCondition"/>, or of a
    /// <see cref="ERenderCommandPatchKind.Wrap"/> in a branch.
    /// </summary>
    public RenderBranchCondition? Condition { get; set; }

    /// <summary>Gets or sets the script text of the push command of a wrap in a state scope, such as <c>bindFBOByName(FrameBufferName="HDR")</c>.</summary>
    public string? WrapScopeText { get; set; }

    /// <summary>Gets or sets the label of the branch that a wrap makes.</summary>
    public string? WrapLabel { get; set; }

    /// <summary>Gets whether the target is a structural path, which can break when the base changes.</summary>
    public bool UsesStructuralPath => RenderCommandPaths.IsStructuralPath(Target);

    /// <summary>Returns a deep copy of this patch.</summary>
    public RenderCommandPatch Clone()
        => new()
        {
            Kind = Kind,
            Target = Target,
            CommandText = CommandText,
            PropertyName = PropertyName,
            ValueText = ValueText,
            Condition = Condition?.Clone(),
            WrapScopeText = WrapScopeText,
            WrapLabel = WrapLabel,
        };

    /// <summary>Returns a short text, such as <c>after(post.bloom)</c>.</summary>
    public string Describe()
        => Kind switch
        {
            ERenderCommandPatchKind.InsertBefore => string.Concat("before(", Target, ")"),
            ERenderCommandPatchKind.InsertAfter => string.Concat("after(", Target, ")"),
            ERenderCommandPatchKind.Replace => string.Concat("replace(", Target, ")"),
            ERenderCommandPatchKind.Remove => string.Concat("remove(", Target, ")"),
            ERenderCommandPatchKind.Wrap => string.Concat("wrap(", Target, ")"),
            ERenderCommandPatchKind.SetProperty => string.Concat("set(", Target, ").", PropertyName),
            ERenderCommandPatchKind.SetCondition => string.Concat("condition(", Target, ")"),
            _ => string.Concat("append(", Target, ")"),
        };

    /// <inheritdoc />
    public override string ToString()
        => Describe();
}
