namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>The change that a <see cref="RenderCommandPatch"/> makes to a base command chain.</summary>
public enum ERenderCommandPatchKind
{
    /// <summary>Adds commands before the target command.</summary>
    InsertBefore,

    /// <summary>Adds commands after the target command. A push target counts with its pop.</summary>
    InsertAfter,

    /// <summary>Replaces the target command with commands.</summary>
    Replace,

    /// <summary>Removes the target command. A push target goes with its pop.</summary>
    Remove,

    /// <summary>Puts the target command in a new state scope or a new branch.</summary>
    Wrap,

    /// <summary>Changes one property of the target command.</summary>
    SetProperty,

    /// <summary>Replaces the condition of the target branch.</summary>
    SetCondition,

    /// <summary>Adds commands at the end of the first child container of the target, or of the root chain.</summary>
    AppendTo,
}
