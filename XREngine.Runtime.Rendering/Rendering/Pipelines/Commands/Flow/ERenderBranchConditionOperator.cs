namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>How a <see cref="RenderBranchCondition"/> compares the source value.</summary>
public enum ERenderBranchConditionOperator
{
    /// <summary>The source value is not zero.</summary>
    IsTrue,

    /// <summary>The source value is zero.</summary>
    IsFalse,

    /// <summary>The source value equals <see cref="RenderBranchCondition.Value"/>.</summary>
    Equal,

    /// <summary>The source value does not equal <see cref="RenderBranchCondition.Value"/>.</summary>
    NotEqual,

    /// <summary>The source value is greater than <see cref="RenderBranchCondition.Value"/>.</summary>
    Greater,

    /// <summary>The source value is greater than or equal to <see cref="RenderBranchCondition.Value"/>.</summary>
    GreaterOrEqual,

    /// <summary>The source value is less than <see cref="RenderBranchCondition.Value"/>.</summary>
    Less,

    /// <summary>The source value is less than or equal to <see cref="RenderBranchCondition.Value"/>.</summary>
    LessOrEqual,
}
