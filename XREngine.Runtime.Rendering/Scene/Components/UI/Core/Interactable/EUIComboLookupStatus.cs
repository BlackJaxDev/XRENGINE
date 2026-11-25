namespace XREngine.Rendering.UI;

/// <summary>Reports the result of a combo keyboard lookup.</summary>
public enum EUIComboLookupStatus
{
    None,
    Selected,
    NoMatch,
    IndexRequired,
    InvalidProviderResult,
    ProviderChanged,
    SelectionRejected,
    NotInteractive
}
