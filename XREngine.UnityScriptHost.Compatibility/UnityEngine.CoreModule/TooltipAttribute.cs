namespace UnityEngine;

/// <summary>Stores field tooltip text as authoring metadata.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class TooltipAttribute : PropertyAttribute
{
    public string tooltip { get; }

    /// <summary>Stores the supplied tooltip text.</summary>
    public TooltipAttribute(string tooltipText) => tooltip = tooltipText;
}
