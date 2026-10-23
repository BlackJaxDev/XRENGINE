namespace XREngine.Rendering.UI;

/// <summary>
/// Shares a backend's possibly one-shot retirement proof across all cohorts
/// retained by the same UI publication receipt.
/// </summary>
internal struct UIReceiptRetirementLatch
{
    public bool IsRetired { get; private set; }
    public bool ShouldQuery => !IsRetired;
    public void Observe(bool backendRetired) => IsRetired |= backendRetired;
}
