namespace XREngine.Rendering.UI;

/// <summary>Pairs a reusable draw contract with one current backend and receipt.</summary>
internal readonly record struct UIStaticDrawCandidate(
    UIStaticDrawContract Contract,
    AbstractRenderer BackendOwner,
    ulong ReceiptId)
{
    internal bool IsDefined => Contract.IsDefined && BackendOwner is not null && ReceiptId != 0;

    internal bool Matches(AbstractRenderer backend, XRMeshRenderer renderer,
        ulong receiptId, uint instances)
        => IsDefined && ReferenceEquals(BackendOwner, backend) &&
            Contract.BackendGeneration == backend.BackendGeneration && ReceiptId == receiptId &&
            Contract.MatchesIntent(renderer, instances);
}
